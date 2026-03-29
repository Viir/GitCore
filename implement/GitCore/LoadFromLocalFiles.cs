using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
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
        if (!Directory.Exists(gitDirectory))
        {
            throw new DirectoryNotFoundException($"Git directory not found: {gitDirectory}");
        }

        var objectsDir = Path.Combine(gitDirectory, "objects");

        if (!Directory.Exists(objectsDir))
        {
            throw new InvalidOperationException($"Not a valid Git directory (missing objects/): {gitDirectory}");
        }

        var allObjects = ImmutableDictionary.CreateBuilder<string, PackFile.PackObject>();

        // Load loose objects
        foreach (var looseObject in LoadLooseObjects(objectsDir))
        {
            allObjects[looseObject.SHA1base16] = looseObject;
        }

        // Load pack files
        var packDir = Path.Combine(objectsDir, "pack");

        if (Directory.Exists(packDir))
        {
            foreach (var packFile in Directory.EnumerateFiles(packDir, "*.pack"))
            {
                var idxFile = Path.ChangeExtension(packFile, ".idx");

                if (File.Exists(idxFile))
                {
                    var packData = (ReadOnlyMemory<byte>)File.ReadAllBytes(packFile);
                    var idxData = (ReadOnlyMemory<byte>)File.ReadAllBytes(idxFile);

                    var indexEntries = PackIndex.ParsePackIndexV2(idxData);
                    var objects = PackFile.ParseAllObjects(packData, indexEntries);

                    foreach (var obj in objects)
                    {
                        allObjects[obj.SHA1base16] = obj;
                    }
                }
                else
                {
                    // No index file - parse directly
                    var packData = (ReadOnlyMemory<byte>)File.ReadAllBytes(packFile);
                    var objects = PackFile.ParseAllObjectsDirectly(packData);

                    foreach (var obj in objects)
                    {
                        allObjects[obj.SHA1base16] = obj;
                    }
                }
            }
        }

        return new Repository(allObjects.ToImmutable());
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
        var refPath = Path.Combine(gitDirectory, reference);

        if (!File.Exists(refPath))
        {
            // Try packed-refs
            return ResolveFromPackedRefs(gitDirectory, reference);
        }

        var content = File.ReadAllText(refPath).Trim();

        // Check if it's a symbolic reference
        if (content.StartsWith("ref: "))
        {
            var targetRef = content[5..];
            return ResolveReference(gitDirectory, targetRef);
        }

        // It's a direct SHA
        if (content.Length == 40 && content.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
        {
            return content;
        }

        return null;
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
        var repository = LoadRepository(gitDirectory);

        var commitObject =
            repository.GetObject(commitSha)
            ?? throw new InvalidOperationException($"Commit {commitSha} not found in repository");

        if (commitObject.Type is not PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {commitSha} is not a commit");
        }

        var commit = GitObjects.ParseCommit(commitObject.Data);

        return
            GitObjects.GetAllFilesFromTree(
                commit.TreeHash,
                sha => repository.GetObject(sha));
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
        var commitSha =
            ResolveHead(gitDirectory)
            ?? throw new InvalidOperationException("Could not resolve HEAD to a commit SHA");

        return LoadTreeContentsFromCommit(gitDirectory, commitSha);
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
        var resolver = CreateLazyObjectResolver(gitDirectory);

        var commitObject =
            resolver(commitSha)
            ?? throw new InvalidOperationException($"Commit {commitSha} not found in repository");

        if (commitObject.Type is not PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {commitSha} is not a commit");
        }

        var commit = GitObjects.ParseCommit(commitObject.Data);

        return
            GitObjects.GetFilesFromSubdirectory(
                commit.TreeHash,
                subdirectoryPath,
                resolver);
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
        var commitSha =
            ResolveHead(gitDirectory)
            ?? throw new InvalidOperationException("Could not resolve HEAD to a commit SHA");

        return LoadSubdirectoryContentsFromCommit(gitDirectory, commitSha, subdirectoryPath);
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

            if (Directory.Exists(candidate) && DirectoryContainsAnyFile(candidate))
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

    /// <summary>
    /// Creates a lazy object resolver function that parses objects on demand from a local
    /// .git directory. Instead of loading all objects upfront, it only reads and decompresses
    /// the specific objects requested via the returned function.
    /// </summary>
    /// <remarks>
    /// The resolver first builds lightweight lookup tables from pack index (.idx) files and
    /// loose object directory entries. When an object is requested by SHA, it is parsed from
    /// the pack file or loose storage and cached for future lookups (including delta chain
    /// resolution).
    /// </remarks>
    private static Func<string, PackFile.PackObject?> CreateLazyObjectResolver(string gitDirectory)
    {
        var objectsDir = Path.Combine(gitDirectory, "objects");

        if (!Directory.Exists(objectsDir))
        {
            throw new InvalidOperationException($"Not a valid Git directory (missing objects/): {gitDirectory}");
        }

        // Build loose object SHA → file path index (without reading file contents)
        var looseObjectPaths = new Dictionary<string, string>();

        foreach (var subDir in Directory.EnumerateDirectories(objectsDir))
        {
            var dirName = Path.GetFileName(subDir);

            if (dirName is "pack" or "info")
                continue;

            if (dirName.Length is not 2)
                continue;

            foreach (var file in Directory.EnumerateFiles(subDir))
            {
                var fileName = Path.GetFileName(file);

                if (fileName.Length is not 38)
                    continue;

                var sha1Hex = dirName + fileName;
                looseObjectPaths[sha1Hex] = file;
            }
        }

        // Build pack file indexes: SHA → (packFilePath, offset) and per-pack metadata
        var packDir = Path.Combine(objectsDir, "pack");

        // Each pack is represented by its pre-loaded data and precomputed lookup structures
        var packInfos =
            new List<(
            byte[] SourceArray,
            Dictionary<string, PackIndex.IndexEntry> EntriesBySHA1,
            Dictionary<int, int> CompressedRegionEnd)>();

        if (Directory.Exists(packDir))
        {
            foreach (var packFile in Directory.EnumerateFiles(packDir, "*.pack"))
            {
                var idxFile = Path.ChangeExtension(packFile, ".idx");

                if (!File.Exists(idxFile))
                    continue;

                var packData = (ReadOnlyMemory<byte>)File.ReadAllBytes(packFile);
                var idxData = (ReadOnlyMemory<byte>)File.ReadAllBytes(idxFile);
                var indexEntries = PackIndex.ParsePackIndexV2(idxData);

                var dataWithoutChecksum = packData[..^20];
                var sourceArray = dataWithoutChecksum.Span.ToArray();

                var entriesBySHA1 = indexEntries.ToDictionary(e => e.SHA1base16, e => e);

                var sortedOffsets = indexEntries.Select(e => (int)e.Offset).OrderBy(o => o).ToArray();
                var compressedRegionEnd = new Dictionary<int, int>(sortedOffsets.Length);

                for (var i = 0; i < sortedOffsets.Length; i++)
                {
                    compressedRegionEnd[sortedOffsets[i]] =
                        (i + 1 < sortedOffsets.Length)
                        ?
                        sortedOffsets[i + 1]
                        :
                        sourceArray.Length;
                }

                packInfos.Add((sourceArray, entriesBySHA1, compressedRegionEnd));
            }
        }

        // Shared cache for parsed objects (supports delta chain resolution across calls)
        var cache = new Dictionary<string, PackFile.PackObject>();

        // Per-pack offset cache for delta chain resolution
        var objectsByOffsetPerPack =
            packInfos.Select(_ => new Dictionary<long, (PackFile.ObjectType Type, byte[] Data)>()).ToArray();

        return
            sha =>
            {
                if (cache.TryGetValue(sha, out var cached))
                {
                    return cached;
                }

                // Try pack files first (most objects live in packs)
                for (var i = 0; i < packInfos.Count; i++)
                {
                    var (sourceArray, entriesBySHA1, compressedRegionEnd) = packInfos[i];

                    if (!entriesBySHA1.TryGetValue(sha, out var entry))
                        continue;

                    var (objectType, data) =
                        PackFile.ParseObjectAtOffset(
                            sourceArray,
                            (int)entry.Offset,
                            compressedRegionEnd,
                            entriesBySHA1,
                            objectsByOffsetPerPack[i]);

                    var packObject = new PackFile.PackObject(objectType, data.Length, data, sha);
                    cache[sha] = packObject;
                    return packObject;
                }

                // Try loose objects
                if (looseObjectPaths.TryGetValue(sha, out var filePath))
                {
                    var compressedData = File.ReadAllBytes(filePath);
                    var decompressedData = DecompressLooseObject(compressedData);
                    var packObject = ParseLooseObjectData(decompressedData, sha);
                    cache[sha] = packObject;
                    return packObject;
                }

                return null;
            };
    }

    private static IEnumerable<PackFile.PackObject> LoadLooseObjects(string objectsDir)
    {
        foreach (var subDir in Directory.EnumerateDirectories(objectsDir))
        {
            var dirName = Path.GetFileName(subDir);

            // Skip special directories
            if (dirName is "pack" or "info")
                continue;

            // Loose object directories are 2 hex characters
            if (dirName.Length is not 2)
                continue;

            foreach (var file in Directory.EnumerateFiles(subDir))
            {
                var fileName = Path.GetFileName(file);

                // Loose object files are 38 hex characters
                if (fileName.Length is not 38)
                    continue;

                var sha1Hex = dirName + fileName;

                var compressedData = File.ReadAllBytes(file);
                var decompressedData = DecompressLooseObject(compressedData);

                yield return ParseLooseObjectData(decompressedData, sha1Hex);
            }
        }
    }

    /// <summary>
    /// Parses a decompressed loose object byte array into a <see cref="PackFile.PackObject"/>.
    /// The data is expected to have the format: "&lt;type&gt; &lt;size&gt;\0&lt;content&gt;".
    /// </summary>
    private static PackFile.PackObject ParseLooseObjectData(byte[] decompressedData, string sha1Hex)
    {
        var nullIndex = Array.IndexOf(decompressedData, (byte)0);

        if (nullIndex < 0)
        {
            throw new InvalidOperationException($"Invalid loose object format for {sha1Hex}");
        }

        var header = Encoding.UTF8.GetString(decompressedData, 0, nullIndex);
        var spaceIndex = header.IndexOf(' ');

        if (spaceIndex < 0)
        {
            throw new InvalidOperationException($"Invalid loose object header for {sha1Hex}: {header}");
        }

        var typeStr = header[..spaceIndex];
        var content = decompressedData.AsMemory()[(nullIndex + 1)..];

        var objectType =
            typeStr switch
            {
                "commit" => PackFile.ObjectType.Commit,
                "tree" => PackFile.ObjectType.Tree,
                "blob" => PackFile.ObjectType.Blob,
                "tag" => PackFile.ObjectType.Tag,

                _ =>
                throw new InvalidOperationException($"Unknown object type: {typeStr}")
            };

        return new PackFile.PackObject(objectType, content.Length, content, sha1Hex);
    }

    private static byte[] DecompressLooseObject(byte[] compressedData)
    {
        using var inputStream = new MemoryStream(compressedData);
        using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();

        zlibStream.CopyTo(outputStream);

        return outputStream.ToArray();
    }

    private static string? ResolveFromPackedRefs(string gitDirectory, string reference)
    {
        var packedRefsPath = Path.Combine(gitDirectory, "packed-refs");

        if (!File.Exists(packedRefsPath))
        {
            return null;
        }

        foreach (var line in File.ReadAllLines(packedRefsPath))
        {
            // Skip comments and peeled refs
            if (line.StartsWith('#') || line.StartsWith('^'))
                continue;

            var parts = line.Split(' ', 2);

            if (parts.Length is 2 && parts[1] == reference)
            {
                return parts[0];
            }
        }

        return null;
    }
}
