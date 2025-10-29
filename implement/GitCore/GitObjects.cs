using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GitCore;

public static class GitObjects
{
    /// <summary>
    /// Represents a participant in a Git commit (author or committer).
    /// </summary>
    public record CommitParticipant(
        string Name,
        string Email,
        DateTimeOffset Timestamp);

    /// <summary>
    /// Represents a Git commit with parsed metadata.
    /// </summary>
    public record CommitObject(
        string TreeSHA1,
        IReadOnlyList<string> ParentSHA1s,
        CommitParticipant Author,
        CommitParticipant Committer,
        string Message);

    public record TreeEntry(
        string Mode,
        string Name,
        string SHA1);

    public record TreeObject(
        IReadOnlyList<TreeEntry> Entries);

    /// <summary>
    /// Parses a Git commit object from its raw byte data.
    /// </summary>
    public static CommitObject ParseCommit(ReadOnlyMemory<byte> data)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        var lines = text.Split('\n');

        string? treeSHA1 = null;
        var parentSHA1s = new List<string>();
        string? authorLine = null;
        string? committerLine = null;
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
                parentSHA1s.Add(line[7..]);
            }
            else if (line.StartsWith("author "))
            {
                authorLine = line[7..];
            }
            else if (line.StartsWith("committer "))
            {
                committerLine = line[10..];
            }
        }

        var message = string.Join("\n", messageLines).TrimEnd('\n');

        if (treeSHA1 is null)
            throw new InvalidOperationException("Commit missing tree");

        if (authorLine is null)
            throw new InvalidOperationException("Commit missing author");

        if (committerLine is null)
            throw new InvalidOperationException("Commit missing committer");

        var author = ParseParticipant(authorLine);
        var committer = ParseParticipant(committerLine);

        return new CommitObject(
            treeSHA1,
            parentSHA1s,
            author,
            committer,
            message);
    }

    /// <summary>
    /// Parses a participant line (author or committer) from a Git commit.
    /// Format: "Name &lt;email&gt; timestamp timezone"
    /// </summary>
    private static CommitParticipant ParseParticipant(string participantLine)
    {
        // Pattern: "Name <email> timestamp timezone"
        // Example: "John Doe <john@example.com> 1234567890 +0000"
        var match = Regex.Match(participantLine, @"^(.+?)\s+<(.+?)>\s+(\d+)\s+([\+\-]\d{4})$");

        if (!match.Success)
        {
            throw new InvalidOperationException($"Invalid participant format: {participantLine}");
        }

        var name = match.Groups[1].Value;
        var email = match.Groups[2].Value;
        var timestampStr = match.Groups[3].Value;
        var timezoneStr = match.Groups[4].Value;

        // Parse Unix timestamp
        var timestamp = long.Parse(timestampStr, CultureInfo.InvariantCulture);
        var dateTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);

        // Parse timezone offset
        var timezoneHours = int.Parse(timezoneStr.Substring(0, 3), CultureInfo.InvariantCulture);
        var timezoneMinutes = int.Parse(timezoneStr.Substring(3, 2), CultureInfo.InvariantCulture);
        var timezoneOffset = new TimeSpan(timezoneHours, timezoneMinutes, 0);

        // Apply timezone offset
        var dateTimeWithOffset = new DateTimeOffset(dateTime.DateTime, timezoneOffset);

        return new CommitParticipant(name, email, dateTimeWithOffset);
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

        if (treeObject.Type is not PackFile.ObjectType.Tree)
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
                    if (blobObject.Type is PackFile.ObjectType.Blob)
                    {
                        files[entry.Name] = GetBlobContent(blobObject.Data);
                    }
                }
            }
            else if (entry.Mode is "40000") // Directory
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

        pathPrefix ??= [];

        var treeObject = getObjectBySHA1(treeSHA1);
        if (treeObject is null)
        {
            throw new InvalidOperationException($"Tree {treeSHA1} not found in pack file");
        }

        if (treeObject.Type is not PackFile.ObjectType.Tree)
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
                if (blobObject is not null && blobObject.Type is PackFile.ObjectType.Blob)
                {
                    files[filePath] = GetBlobContent(blobObject.Data);
                }
            }
            else if (entry.Mode is "40000") // Directory
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

            if (treeObject.Type is not PackFile.ObjectType.Tree)
            {
                throw new InvalidOperationException($"Object {currentTreeSHA1} is not a tree");
            }

            var tree = ParseTree(treeObject.Data);
            var entry = tree.Entries.FirstOrDefault(e => e.Name == pathComponent);

            if (entry is null)
            {
                throw new InvalidOperationException($"Path component '{pathComponent}' not found in tree");
            }

            if (entry.Mode is not "40000")
            {
                throw new InvalidOperationException($"Path component '{pathComponent}' is not a directory");
            }

            currentTreeSHA1 = entry.SHA1;
        }

        // Now get all files from the subdirectory tree
        return GetAllFilesFromTree(currentTreeSHA1, getObjectBySHA1, pathPrefix: []);
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

        if (commitObject.Type is not PackFile.ObjectType.Commit)
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
