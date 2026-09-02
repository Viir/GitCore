using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;

namespace GitCore;

using FilePath = IReadOnlyList<string>;

/// <summary>
/// Loads Git repository data from a local .git directory.
/// </summary>
public static class LoadFromLocalFiles
{
    /// <summary>
    /// Opens a local Git repository and loads all objects into an in-memory Repository.
    /// </summary>
    /// <param name="gitDirectory">
    /// Absolute or relative path to the .git directory.
    /// For standard repositories, this is the .git folder inside the worktree.
    /// For bare repositories, this is the repository root.
    /// </param>
    /// <returns>A Repository containing all objects found in the local .git directory.</returns>
    public static Repository LoadRepository(string gitDirectory)
    {
        using var localRepository =
            LocalGitRepository.Open(
                gitDirectory,
                new LocalRepositoryOptions
                {
                    CacheBlobContents = true,
                    MaximumCachedObjectBytes = null,
                    MaximumCachedObjectCount = null
                });

        var allObjects =
            localRepository.ObjectIds
            .Select(localRepository.GetRequiredObject)
            .ToImmutableDictionary(
                gitObject => gitObject.ObjectId,
                gitObject =>
                new PackFile.PackObject(
                    gitObject.Type,
                    gitObject.Size,
                    gitObject.Data,
                    gitObject.ObjectId),
                StringComparer.Ordinal);

        return new Repository(allObjects);
    }

    /// <summary>
    /// Resolves HEAD to a commit SHA from a local .git directory.
    /// This is a convenience method that calls <see cref="ResolveReference"/> with "HEAD".
    /// </summary>
    /// <param name="gitDirectory">Path to the .git directory.</param>
    /// <returns>The 40-character hex commit SHA, or null if HEAD cannot be resolved.</returns>
    public static string? ResolveHead(string gitDirectory)
    {
        return ResolveReference(gitDirectory, "HEAD");
    }

    /// <summary>
    /// Resolves a reference to a commit SHA from a local .git directory.
    /// Follows symbolic references (e.g., HEAD → refs/heads/main → commit SHA).
    /// </summary>
    /// <param name="gitDirectory">Path to the .git directory.</param>
    /// <param name="reference">
    /// The reference to resolve.
    /// Can be "HEAD", "refs/heads/main", "refs/tags/v1.0", etc.
    /// </param>
    /// <returns>The 40-character hex commit SHA, or null if the reference cannot be resolved.</returns>
    public static string? ResolveReference(string gitDirectory, string reference)
    {
        return LocalGitRepository.ResolveReferenceAtPath(gitDirectory, reference);
    }

    /// <summary>
    /// Loads all file contents from the tree of a specific commit in a local repository.
    /// </summary>
    /// <param name="gitDirectory">Path to the .git directory.</param>
    /// <param name="commitSha">
    /// The 40-character hex SHA of the commit.
    /// Use <see cref="ResolveReference"/> to obtain this from HEAD or a branch name.
    /// </param>
    /// <returns>
    /// A dictionary mapping file paths (as lists of path components) to file contents.
    /// Only blob (file) entries are included; directory structure is flattened into paths.
    /// </returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromCommit(
        string gitDirectory,
        string commitSha)
    {
        using var repository = LocalGitRepository.Open(gitDirectory);
        return MaterializeFiles(repository, repository.EnumerateTree(commitSha));
    }

    /// <summary>
    /// Loads all file contents from the tree at the current HEAD of a local repository.
    /// This is a convenience method that resolves HEAD and then loads the tree.
    /// </summary>
    /// <param name="gitDirectory">Path to the .git directory.</param>
    /// <returns>
    /// A dictionary mapping file paths (as lists of path components) to file contents.
    /// </returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromHead(
        string gitDirectory)
    {
        using var repository = LocalGitRepository.Open(gitDirectory);

        var commitSha =
            repository.ResolveHead()
            ?? throw new InvalidOperationException("Could not resolve HEAD to a commit SHA");

        return MaterializeFiles(repository, repository.EnumerateTree(commitSha));
    }

    /// <summary>
    /// Loads file contents from a subdirectory within the tree of a specific commit
    /// in a local repository. Only blobs under the specified subdirectory are materialized.
    /// <para>
    /// This method uses lazy (on-demand) object loading: instead of parsing every object
    /// in the repository, it only parses the objects actually needed to traverse from the
    /// commit to the requested subdirectory and read its blobs. This is much faster for
    /// repositories with large pack files.
    /// </para>
    /// </summary>
    /// <param name="gitDirectory">Path to the .git directory.</param>
    /// <param name="commitSha">
    /// The 40-character hex SHA of the commit.
    /// Use <see cref="ResolveReference"/> to obtain this from HEAD or a branch name.
    /// </param>
    /// <param name="subdirectoryPath">
    /// Path components from the repository root to the subdirectory to load.
    /// For example, ["implement", "GitCore"] loads only files under implement/GitCore/.
    /// </param>
    /// <returns>
    /// A dictionary mapping file paths (relative to the subdirectory, as lists of path
    /// components) to file contents. Only blob entries are included.
    /// </returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadSubdirectoryContentsFromCommit(
        string gitDirectory,
        string commitSha,
        IReadOnlyList<string> subdirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(subdirectoryPath);
        using var repository = LocalGitRepository.Open(gitDirectory);
        return LoadSubdirectoryContents(repository, commitSha, subdirectoryPath);
    }

    /// <summary>
    /// Loads file contents from a subdirectory within the tree at the current HEAD
    /// of a local repository. Only blobs under the specified subdirectory are materialized.
    /// This is a convenience method that resolves HEAD and then loads the subdirectory.
    /// </summary>
    /// <param name="gitDirectory">Path to the .git directory.</param>
    /// <param name="subdirectoryPath">
    /// Path components from the repository root to the subdirectory to load.
    /// For example, ["implement", "GitCore"] loads only files under implement/GitCore/.
    /// </param>
    /// <returns>
    /// A dictionary mapping file paths (relative to the subdirectory, as lists of path
    /// components) to file contents. Only blob entries are included.
    /// </returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadSubdirectoryContentsFromHead(
        string gitDirectory,
        IReadOnlyList<string> subdirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(subdirectoryPath);
        using var repository = LocalGitRepository.Open(gitDirectory);

        var commitSha =
            repository.ResolveHead()
            ?? throw new InvalidOperationException("Could not resolve HEAD to a commit SHA");

        return LoadSubdirectoryContents(repository, commitSha, subdirectoryPath);
    }

    /// <summary>
    /// Computes the SHA-1 hash of a Git tree object from its entries.
    /// This produces the same hash that Git would compute for an equivalent tree.
    /// </summary>
    /// <param name="entries">The tree entries to hash.</param>
    /// <returns>The 40-character hex SHA-1 hash of the tree.</returns>
    public static string ComputeTreeSha(IReadOnlyList<GitObjects.TreeEntry> entries)
    {
        // Build the tree data in Git's binary format:
        // For each entry: "<mode> <name>\0<20-byte-hash>"
        using var stream = new MemoryStream();

        foreach (var entry in entries)
        {
            var header = Encoding.UTF8.GetBytes($"{entry.Mode} {entry.Name}\0");
            stream.Write(header);

            var hashBytes = Convert.FromHexString(entry.HashBase16);
            stream.Write(hashBytes);
        }

        var treeData = stream.ToArray();

        // Compute SHA1 of "tree <size>\0<data>"
        var headerStr = $"tree {treeData.Length}\0";
        var headerBytes = Encoding.UTF8.GetBytes(headerStr);
        var fullData = new byte[headerBytes.Length + treeData.Length];
        Array.Copy(headerBytes, fullData, headerBytes.Length);
        Array.Copy(treeData, 0, fullData, headerBytes.Length, treeData.Length);

        // SHA-1 is used intentionally here for Git protocol compatibility, not for security purposes.
        var sha1 = System.Security.Cryptography.SHA1.HashData(fullData);
        return Convert.ToHexStringLower(sha1);
    }

    /// <summary>
    /// Searches upward from the given <paramref name="startPath"/> for the first directory
    /// named <c>.git</c> that contains at least one file (recursively).
    /// <para>
    /// The search begins at <paramref name="startPath"/> itself (or its parent directory if
    /// <paramref name="startPath"/> is a file) and walks up the directory tree toward the
    /// filesystem root. At each level, the method checks whether a subdirectory named
    /// <c>.git</c> exists and whether it contains at least one file anywhere inside it.
    /// An empty <c>.git</c> directory (or one that contains only empty subdirectories) is
    /// not considered a valid Git directory and is skipped.
    /// </para>
    /// <para>
    /// If <paramref name="startPath"/> does not exist or is not a valid path, the method
    /// returns <c>null</c> and <paramref name="checkedPaths"/> will be empty.
    /// </para>
    /// </summary>
    /// <param name="startPath">
    /// The path from which to begin searching. This may be a directory, a file, or a path
    /// that does not exist. If it is a file, the search starts from its containing directory.
    /// </param>
    /// <param name="checkedPaths">
    /// When the method returns, contains the list of <c>.git</c> candidate paths that were
    /// inspected during the search, in the order they were checked (from the starting
    /// directory upward). Each entry is the full path of the <c>.git</c> directory that was
    /// examined, regardless of whether it was valid.
    /// </param>
    /// <returns>
    /// The full path to the first valid <c>.git</c> directory found, or <c>null</c> if no
    /// valid Git directory was found before reaching the filesystem root.
    /// </returns>
    public static string? FindGitDirectoryUpwards(
        string startPath,
        out IReadOnlyList<string> checkedPaths)
    {
        var checkedCandidates = new List<string>();
        checkedPaths = checkedCandidates;

        var current = ResolveStartDirectory(startPath);

        if (current is null)
            return null;

        while (current is not null)
        {
            var candidate = Path.Combine(current, ".git");

            checkedCandidates.Add(candidate);

            if ((Directory.Exists(candidate) && DirectoryContainsAnyFile(candidate)) ||
                (File.Exists(candidate) &&
                File.ReadAllText(candidate).TrimStart().StartsWith(
                    "gitdir:",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Resolves the starting directory from a path that may point to a file, a directory,
    /// or a location that does not exist.
    /// Returns <c>null</c> when no usable directory can be determined.
    /// </summary>
    private static string? ResolveStartDirectory(string path)
    {
        if (Directory.Exists(path))
            return Path.GetFullPath(path);

        if (File.Exists(path))
            return Path.GetDirectoryName(Path.GetFullPath(path));

        return null;
    }

    /// <summary>
    /// Returns <c>true</c> if the given directory contains at least one file, searching
    /// recursively through all subdirectories.
    /// </summary>
    private static bool DirectoryContainsAnyFile(string directoryPath)
    {
        return Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories).Any();
    }

    private static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> MaterializeFiles(
        LocalGitRepository repository,
        IEnumerable<GitTreeFile> files)
    {
        var materialized =
            new Dictionary<FilePath, ReadOnlyMemory<byte>>(
                comparer: Common.EnumerableExtensions.EqualityComparer<FilePath>());

        foreach (var file in files)
        {
            materialized[file.Path] = repository.GetRequiredObject(file.ObjectId).Data;
        }

        return materialized;
    }

    private static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadSubdirectoryContents(
        LocalGitRepository repository,
        string commitSha,
        IReadOnlyList<string> subdirectoryPath)
    {
        var prefix = subdirectoryPath.ToArray();
        ValidateSubdirectory(repository, commitSha, prefix);

        var files =
            repository.EnumerateTree(
                commitSha,
                new TreeTraversalOptions
                {
                    SelectFile =
                    file =>
                    IsPathPrefix(prefix, file.Path) &&
                    file.Path.Count > prefix.Length
                    ?
                    TreeFileSelection.Include
                    :
                    TreeFileSelection.Skip,
                    SelectSubtree =
                    subtree =>
                    IsPathPrefix(subtree.Path, prefix) ||
                    IsPathPrefix(prefix, subtree.Path)
                    ?
                    TreeSubtreeSelection.Descend
                    :
                    TreeSubtreeSelection.Skip
                });

        var materialized =
            new Dictionary<FilePath, ReadOnlyMemory<byte>>(
                comparer: Common.EnumerableExtensions.EqualityComparer<FilePath>());

        foreach (var file in files)
        {
            materialized[[.. file.Path.Skip(prefix.Length)]] =
                repository.GetRequiredObject(file.ObjectId).Data;
        }

        return materialized;
    }

    private static void ValidateSubdirectory(
        LocalGitRepository repository,
        string commitSha,
        IReadOnlyList<string> subdirectoryPath)
    {
        if (subdirectoryPath.Count is 0)
            return;

        var commitObject = repository.GetRequiredObject(commitSha);

        if (commitObject.Type is not PackFile.ObjectType.Commit)
            throw new InvalidOperationException($"Object {commitSha} is not a commit");

        var currentTreeHash = GitObjects.ParseCommit(commitObject.Data).TreeHash;

        foreach (var component in subdirectoryPath)
        {
            var treeObject = repository.GetRequiredObject(currentTreeHash);

            if (treeObject.Type is not PackFile.ObjectType.Tree)
                throw new InvalidOperationException($"Object {currentTreeHash} is not a tree");

            var entry =
                GitObjects.ParseTree(treeObject.Data).Entries
                .FirstOrDefault(candidate => candidate.Name == component)
                ?? throw new InvalidOperationException(
                    $"Path component '{component}' not found in tree");

            if (entry.Mode is not "40000")
                throw new InvalidOperationException($"Path component '{component}' is not a directory");

            currentTreeHash = entry.HashBase16;
        }
    }

    private static bool IsPathPrefix(
        IReadOnlyList<string> prefix,
        IReadOnlyList<string> path)
    {
        return
            prefix.Count <= path.Count &&
            prefix.Select((component, index) => component == path[index]).All(matches => matches);
    }
}
