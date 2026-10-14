using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GitCore;

/// <summary>
/// Controls working-tree queries and application-owned parsing caches.
/// </summary>
public sealed class GitWorkingTreeOptions
{
    /// <summary>
    /// Gets a parser optionally backed by a cache keyed by the complete raw ignore-file content.
    /// Parsed files are immutable and can be shared among repositories.
    /// </summary>
    public Func<string, GitIgnoreFile>? ParseIgnoreFile { get; init; }

    /// <summary>
    /// Overrides core.excludesFile and the default user ignore file.
    /// An empty string disables user-wide excludes; a relative path is resolved from the worktree root.
    /// </summary>
    public string? GlobalExcludesFilePath { get; init; }

    /// <summary>
    /// Overrides core.ignoreCase. By default queries use the repository's configuration,
    /// not the host operating system's case sensitivity.
    /// </summary>
    public bool? IgnoreCase { get; init; }
}

/// <summary>
/// Queries a working tree without invoking Git or loading committed blobs.
/// An instance snapshots configuration and lazily snapshots the index. Ignore-file contents are
/// read on each query; unchanged contents reuse their parsed representation.
/// Instances are not intended for concurrent use.
/// </summary>
public sealed class GitWorkingTree
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly IGitFileSystem _fileSystem;
    private readonly Func<string, GitIgnoreFile> _parseIgnoreFile;
    private readonly Dictionary<string, (string Content, GitIgnoreFile Parsed)> _ignoreFiles = new(StringComparer.Ordinal);
    private readonly string? _globalExcludesFile;
    private readonly int _objectIdLength;
    private GitIndex? _index;
    private HashSet<string>? _gitlinks;

    private GitWorkingTree(
        string rootDirectory,
        string gitDirectory,
        string commonGitDirectory,
        IGitFileSystem fileSystem,
        GitWorkingTreeOptions options)
    {
        RootDirectory = rootDirectory;
        GitDirectory = gitDirectory;
        CommonGitDirectory = commonGitDirectory;
        _fileSystem = fileSystem;
        _parseIgnoreFile = options.ParseIgnoreFile ?? GitIgnoreFile.Parse;

        var configuration = GitWorkingTreeConfiguration.Read(this, fileSystem);
        IgnoreCase = options.IgnoreCase ?? configuration.IgnoreCase;
        _objectIdLength = configuration.ObjectIdLength;
        var excludesFile = options.GlobalExcludesFilePath ?? configuration.ExcludesFile;

        _globalExcludesFile = string.IsNullOrEmpty(excludesFile)
            ? null
            : Path.GetFullPath(excludesFile, rootDirectory);
    }

    /// <summary>Gets the absolute working-tree root.</summary>
    public string RootDirectory { get; }

    /// <summary>Gets the absolute per-worktree Git directory containing the index.</summary>
    public string GitDirectory { get; }

    /// <summary>Gets the shared Git directory containing config and info/exclude.</summary>
    public string CommonGitDirectory { get; }

    /// <summary>Gets whether queries use case-insensitive Git path matching.</summary>
    public bool IgnoreCase { get; }

    /// <summary>
    /// Opens a working-tree root containing a .git directory or gitdir file.
    /// Bare repositories do not have formatting candidates and are not accepted.
    /// </summary>
    public static GitWorkingTree Open(
        string rootDirectory,
        IGitFileSystem? fileSystem = null,
        GitWorkingTreeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var storage = fileSystem ?? PhysicalGitFileSystem.Instance;
        var gitPath = Path.Combine(root, ".git");
        var kind = storage.GetEntryKind(gitPath);

        var gitDirectory =
            kind switch
            {
                GitFileSystemEntryKind.Directory => gitPath,
                GitFileSystemEntryKind.File => ResolveGitFile(gitPath, storage),
                _ => throw new DirectoryNotFoundException($"No working-tree .git entry exists in {root}.")
            };

        if (storage.GetEntryKind(gitDirectory) is not GitFileSystemEntryKind.Directory)
            throw new DirectoryNotFoundException($"Git directory {gitDirectory} does not exist.");

        var commonDirectory = gitDirectory;
        var commonFile = storage.ReadAllBytes(Path.Combine(gitDirectory, "commondir"));

        if (commonFile is not null)
        {
            var value = Decode(commonFile).Trim();

            if (value.Length is 0)
                throw new InvalidDataException("The Git commondir file is empty.");

            commonDirectory = Path.GetFullPath(value, gitDirectory);

            if (storage.GetEntryKind(commonDirectory) is not GitFileSystemEntryKind.Directory)
                throw new DirectoryNotFoundException($"Common Git directory {commonDirectory} does not exist.");
        }

        return new GitWorkingTree(root, gitDirectory, commonDirectory, storage, options ?? new());
    }

    /// <summary>
    /// Finds the nearest working tree at or above a file or directory path.
    /// Returns null outside a working tree. A malformed .git entry is an error, not a reason to
    /// continue searching an enclosing repository.
    /// </summary>
    public static GitWorkingTree? Discover(
        string path,
        IGitFileSystem? fileSystem = null,
        GitWorkingTreeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var storage = fileSystem ?? PhysicalGitFileSystem.Instance;
        var current = Path.GetFullPath(path);

        if (storage.GetEntryKind(current) is not GitFileSystemEntryKind.Directory)
            current = Path.GetDirectoryName(current);

        while (current is not null)
        {
            if (storage.GetEntryKind(Path.Combine(current, ".git")) is not GitFileSystemEntryKind.Missing)
                return Open(current, storage, options);

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    /// Gets the staging-area snapshot. Missing indexes (including unborn repositories) are empty.
    /// Tracking is based on the index, not HEAD: staged additions and conflicts are tracked,
    /// while staged removals are not.
    /// </summary>
    public GitIndex ReadIndex()
    {
        if (_index is not null)
            return _index;

        var bytes = _fileSystem.ReadAllBytes(Path.Combine(GitDirectory, "index"));

        _index = bytes is null
            ? GitIndex.Empty
            : GitIndex.Parse(
                bytes,
                _objectIdLength,
                checksum =>
                    _fileSystem.ReadAllBytes(Path.Combine(GitDirectory, "sharedindex." + checksum))
                    ?? throw new InvalidDataException($"Shared Git index {checksum} is missing."));

        _gitlinks = new HashSet<string>(IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var entry in _index.Entries)
        {
            if ((entry.Mode & 0xf000) == 0xe000)
                _gitlinks.Add(entry.Path);
        }

        return _index;
    }

    /// <summary>Discards the cached index; the next tracking query reads it again.</summary>
    public void RefreshIndex()
    {
        _index = null;
        _gitlinks = null;
    }

    /// <summary>
    /// Gets whether a path has an index entry, even if missing from disk or covered by ignore rules.
    /// Accepts an absolute native path or a path relative to the working-tree root.
    /// Paths outside the tree are rejected.
    /// </summary>
    public bool IsTracked(string path)
    {
        var relative = GetRelativePath(path);
        return relative.Length is not 0 && ReadIndex().IsTracked(relative, IgnoreCase);
    }

    /// <summary>
    /// Evaluates ignore rules regardless of tracking, analogous to git check-ignore --no-index.
    /// Reads nested .gitignore files, info/exclude and user-wide excludes, in Git precedence order.
    /// Excluded parent directories cannot be re-included by a rule for a child.
    /// Set isDirectory for hypothetical directories that do not exist on disk.
    /// </summary>
    public bool IsIgnored(string path, bool isDirectory = false)
    {
        var relativePath = GetRelativePath(path);

        if (relativePath.Length is 0)
            return false;

        var components = relativePath.Split('/');
        var rules = new List<(string Directory, GitIgnoreFile File)>();
        AddIgnoreFile(rules, _globalExcludesFile, "");
        AddIgnoreFile(rules, Path.Combine(CommonGitDirectory, "info", "exclude"), "");
        var directory = "";

        for (var i = 0; i < components.Length; ++i)
        {
            AddIgnoreFile(rules, Path.Combine(ToAbsolutePath(directory), ".gitignore"), directory);
            var candidate = directory.Length is 0 ? components[i] : directory + "/" + components[i];
            var kind = _fileSystem.GetEntryKind(ToAbsolutePath(candidate));
            var candidateIsDirectory =
                kind is not GitFileSystemEntryKind.SymbolicLink &&
                (i < components.Length - 1 || isDirectory || kind is GitFileSystemEntryKind.Directory);
            var ignored = false;

            foreach (var (baseDirectory, file) in rules)
            {
                var fromBase = baseDirectory.Length is 0 ? candidate : candidate[(baseDirectory.Length + 1)..];
                var match = file.MatchPath(fromBase, candidateIsDirectory, IgnoreCase);

                if (match.HasValue)
                    ignored = match.Value;
            }

            if (ignored)
                return true;

            // Git does not descend into symlinked directories or consult their .gitignore files.
            if (kind is GitFileSystemEntryKind.SymbolicLink && i < components.Length - 1)
                return false;

            directory = candidate;
        }

        return false;
    }

    /// <summary>
    /// Gets whether an existing regular file is a formatting candidate: tracked, or untracked and
    /// not ignored. Excludes Git metadata, directories, symlinks, and files below nested repositories.
    /// This is a selection predicate, not a test for content changes or a replacement for git status.
    /// Tracked files remain candidates even when covered by ignore rules.
    /// </summary>
    public bool ShouldIncludeFile(string path)
    {
        var relativePath = GetRelativePath(path);

        if (relativePath.Length is 0)
            return false;

        var components = relativePath.Split('/');
        var directory = RootDirectory;

        for (var i = 0; i < components.Length; ++i)
        {
            if (components[i].Equals(".git", StringComparison.OrdinalIgnoreCase))
                return false;

            directory = Path.Combine(directory, components[i]);
            var kind = _fileSystem.GetEntryKind(directory);

            if (i == components.Length - 1)
            {
                if (kind is not GitFileSystemEntryKind.File)
                    return false;
            }
            else
            {
                if (kind is not GitFileSystemEntryKind.Directory ||
                    _fileSystem.GetEntryKind(Path.Combine(directory, ".git")) is not GitFileSystemEntryKind.Missing)
                    return false;
            }
        }

        var absolutePath = ToAbsolutePath(relativePath);

        if (IsWithin(absolutePath, GitDirectory) || IsWithin(absolutePath, CommonGitDirectory))
            return false;

        ReadIndex();
        var ancestor = "";

        for (var i = 0; i < components.Length - 1; ++i)
        {
            ancestor = ancestor.Length is 0 ? components[i] : ancestor + "/" + components[i];

            if (_gitlinks!.Contains(ancestor))
                return false;
        }

        return IsTracked(relativePath) || !IsIgnored(relativePath);
    }

    private string GetRelativePath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (path.Contains('\0'))
            throw new ArgumentException("Paths cannot contain NUL.", nameof(path));

        var absolute = Path.GetFullPath(path, RootDirectory);
        var relative = Path.GetRelativePath(RootDirectory, absolute);

        if (relative == ".")
            return "";

        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The path must be inside the working tree.", nameof(path));

        return Path.DirectorySeparatorChar == '/'
            ? relative
            : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private string ToAbsolutePath(string relativePath)
    {
        return Path.GetFullPath(relativePath.Replace('/', Path.DirectorySeparatorChar), RootDirectory);
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void AddIgnoreFile(
        List<(string Directory, GitIgnoreFile File)> rules,
        string? absolutePath,
        string directory)
    {
        if (absolutePath is null ||
            _fileSystem.GetEntryKind(absolutePath) is not GitFileSystemEntryKind.File)
            return;

        var bytes = _fileSystem.ReadAllBytes(absolutePath);

        if (bytes is null)
            return;

        var content = s_utf8.GetString(bytes);

        if (!_ignoreFiles.TryGetValue(absolutePath, out var cached) || cached.Content != content)
        {
            cached = (content, _parseIgnoreFile(content));
            _ignoreFiles[absolutePath] = cached;
        }

        rules.Add((directory, cached.Parsed));
    }

    private static string ResolveGitFile(string path, IGitFileSystem fileSystem)
    {
        var bytes = fileSystem.ReadAllBytes(path)
            ?? throw new InvalidDataException($"Git file {path} disappeared.");
        var content = Decode(bytes).Trim();
        const string Prefix = "gitdir:";

        if (!content.StartsWith(Prefix, StringComparison.Ordinal) || content[Prefix.Length..].Trim().Length is 0)
            throw new InvalidDataException($"Git file {path} does not contain a gitdir directive.");

        return Path.GetFullPath(content[Prefix.Length..].Trim(), Path.GetDirectoryName(path)!);
    }

    internal static string Decode(byte[] bytes)
    {
        return s_utf8.GetString(bytes).TrimStart('\uFEFF');
    }
}
