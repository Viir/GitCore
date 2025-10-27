using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitCore;

public static class GitObjects
{
    public record CommitObject(
        string TreeSHA1,
        string? ParentSHA1,
        string Author,
        string Committer,
        string Message);

    public record TreeEntry(
        string Mode,
        string Name,
        string SHA1);

    public record TreeObject(
        IReadOnlyList<TreeEntry> Entries);

    public static CommitObject ParseCommit(ReadOnlyMemory<byte> data)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        var lines = text.Split('\n');

        string? treeSHA1 = null;
        string? parentSHA1 = null;
        string? author = null;
        string? committer = null;
        var messageLines = new List<string>();
        var inMessage = false;

        foreach (var line in lines)
        {
            if (inMessage)
            {
                messageLines.Add(line);
            }
            else if (string.IsNullOrEmpty(line))
            {
                inMessage = true;
            }
            else if (line.StartsWith("tree "))
            {
                treeSHA1 = line[5..];
            }
            else if (line.StartsWith("parent "))
            {
                parentSHA1 = line[7..];
            }
            else if (line.StartsWith("author "))
            {
                author = line[7..];
            }
            else if (line.StartsWith("committer "))
            {
                committer = line[10..];
            }
        }

        var message = string.Join("\n", messageLines).TrimEnd('\n');

        return new CommitObject(
            treeSHA1 ?? throw new InvalidOperationException("Commit missing tree"),
            parentSHA1,
            author ?? throw new InvalidOperationException("Commit missing author"),
            committer ?? throw new InvalidOperationException("Commit missing committer"),
            message);
    }

    public static TreeObject ParseTree(ReadOnlyMemory<byte> data)
    {
        var entries = new List<TreeEntry>();
        var span = data.Span;
        var offset = 0;

        while (offset < span.Length)
        {
            // Read mode (e.g., "100644")
            var modeEnd = offset;

            while (modeEnd < span.Length && span[modeEnd] is not 32)
            {
                modeEnd++;
            }

            var mode = Encoding.UTF8.GetString(span[offset..modeEnd]);
            offset = modeEnd + 1; // Skip space

            // Read name
            var nameEnd = offset;

            while (nameEnd < span.Length && span[nameEnd] is not 0)
            {
                nameEnd++;
            }

            var name = Encoding.UTF8.GetString(span[offset..nameEnd]);
            offset = nameEnd + 1; // Skip null byte

            // Read SHA1 (20 bytes)
            var sha1Bytes = span.Slice(offset, 20);
            var sha1 = Convert.ToHexStringLower(sha1Bytes);
            offset += 20;

            entries.Add(new TreeEntry(mode, name, sha1));
        }

        return new TreeObject(entries);
    }

    public static ReadOnlyMemory<byte> GetBlobContent(ReadOnlyMemory<byte> data)
    {
        return data;
    }

    public static IReadOnlyDictionary<string, ReadOnlyMemory<byte>> GetFilesFromTree(
        string treeSHA1,
        IReadOnlyDictionary<string, PackFile.PackObject> objectsBySHA1)
    {
        var files = new Dictionary<string, ReadOnlyMemory<byte>>();

        if (!objectsBySHA1.TryGetValue(treeSHA1, out var treeObject))
        {
            throw new InvalidOperationException($"Tree {treeSHA1} not found in pack file");
        }

        if (treeObject.Type != PackFile.ObjectType.Tree)
        {
            throw new InvalidOperationException($"Object {treeSHA1} is not a tree");
        }

        var tree = ParseTree(treeObject.Data);

        foreach (var entry in tree.Entries)
        {
            if (entry.Mode.StartsWith("100")) // Regular file
            {
                if (objectsBySHA1.TryGetValue(entry.SHA1, out var blobObject))
                {
                    if (blobObject.Type == PackFile.ObjectType.Blob)
                    {
                        files[entry.Name] = GetBlobContent(blobObject.Data);
                    }
                }
            }
            else if (entry.Mode == "40000") // Directory
            {
                // For now, we'll skip subdirectories
                // In a full implementation, we would recursively process them
            }
        }

        return files;
    }

    public static IReadOnlyDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> GetAllFilesFromTree(
        string treeSHA1,
        Func<string, PackFile.PackObject?> getObjectBySHA1,
        IReadOnlyList<string>? pathPrefix = null)
    {
        var files = new Dictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>>(
            comparer: Common.EnumerableExtensions.EqualityComparer<IReadOnlyList<string>>());

        pathPrefix ??= Array.Empty<string>();

        var treeObject = getObjectBySHA1(treeSHA1);
        if (treeObject is null)
        {
            throw new InvalidOperationException($"Tree {treeSHA1} not found in pack file");
        }

        if (treeObject.Type != PackFile.ObjectType.Tree)
        {
            throw new InvalidOperationException($"Object {treeSHA1} is not a tree");
        }

        var tree = ParseTree(treeObject.Data);

        foreach (var entry in tree.Entries)
        {
            var filePath = pathPrefix.Concat([entry.Name]).ToArray();

            if (entry.Mode.StartsWith("100")) // Regular file
            {
                var blobObject = getObjectBySHA1(entry.SHA1);
                if (blobObject is not null && blobObject.Type == PackFile.ObjectType.Blob)
                {
                    files[filePath] = GetBlobContent(blobObject.Data);
                }
            }
            else if (entry.Mode == "40000") // Directory
            {
                // Recursively process subdirectories
                var subFiles = GetAllFilesFromTree(entry.SHA1, getObjectBySHA1, filePath);
                foreach (var (subPath, content) in subFiles)
                {
                    files[subPath] = content;
                }
            }
        }

        return files;
    }

    /// <summary>
    /// Gets files from a specific subdirectory within a tree.
    /// </summary>
    /// <param name="treeSHA1">The SHA1 of the root tree</param>
    /// <param name="subdirectoryPath">Path components to the subdirectory (e.g., ["implement", "GitCore"])</param>
    /// <param name="getObjectBySHA1">Function to retrieve objects by SHA1</param>
    /// <returns>Dictionary of file paths (relative to subdirectory) to their contents</returns>
    public static IReadOnlyDictionary<IReadOnlyList<string>, ReadOnlyMemory<byte>> GetFilesFromSubdirectory(
        string treeSHA1,
        IReadOnlyList<string> subdirectoryPath,
        Func<string, PackFile.PackObject?> getObjectBySHA1)
    {
        // Navigate to the subdirectory by traversing the tree
        var currentTreeSHA1 = treeSHA1;

        foreach (var pathComponent in subdirectoryPath)
        {
            var treeObject = getObjectBySHA1(currentTreeSHA1);
            if (treeObject is null)
            {
                throw new InvalidOperationException($"Tree {currentTreeSHA1} not found");
            }

            if (treeObject.Type != PackFile.ObjectType.Tree)
            {
                throw new InvalidOperationException($"Object {currentTreeSHA1} is not a tree");
            }

            var tree = ParseTree(treeObject.Data);
            var entry = tree.Entries.FirstOrDefault(e => e.Name == pathComponent);

            if (entry is null)
            {
                throw new InvalidOperationException($"Path component '{pathComponent}' not found in tree");
            }

            if (entry.Mode != "40000")
            {
                throw new InvalidOperationException($"Path component '{pathComponent}' is not a directory");
            }

            currentTreeSHA1 = entry.SHA1;
        }

        // Now get all files from the subdirectory tree
        return GetAllFilesFromTree(currentTreeSHA1, getObjectBySHA1, pathPrefix: Array.Empty<string>());
    }

    public static ReadOnlyMemory<byte> GetFileFromCommit(
        string commitSHA1,
        string fileName,
        IReadOnlyDictionary<string, PackFile.PackObject> objectsBySHA1)
    {
        if (!objectsBySHA1.TryGetValue(commitSHA1, out var commitObject))
        {
            throw new InvalidOperationException($"Commit {commitSHA1} not found in pack file");
        }

        if (commitObject.Type != PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {commitSHA1} is not a commit");
        }

        var commit = ParseCommit(commitObject.Data);
        var files = GetFilesFromTree(commit.TreeSHA1, objectsBySHA1);

        if (!files.TryGetValue(fileName, out var fileContent))
        {
            throw new InvalidOperationException($"File {fileName} not found in tree");
        }

        return fileContent;
    }
}
