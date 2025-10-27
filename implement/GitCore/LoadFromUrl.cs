using System;
using System.Collections.Generic;

namespace GitCore;

using FilePath = IReadOnlyList<string>;

/// <summary>
/// Loads Git tree contents from remote URLs.
/// </summary>
public class LoadFromUrl
{
    /// <summary>
    /// Loads the contents of a Git tree from a GitHub or GitLab URL.
    /// </summary>
    /// <param name="url">A tree URL like https://github.com/owner/repo/tree/commit-sha</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromUrl(string url)
    {
        // Parse the URL to extract repository information and commit SHA
        var (baseUrl, owner, repo, commitSha) = GitSmartHttp.ParseTreeUrl(url);

        // Fetch the pack file containing the commit and its tree
        var packFileData = GitSmartHttp.FetchPackFileAsync(baseUrl, owner, repo, commitSha)
            .GetAwaiter()
            .GetResult();

        // Generate index for the pack file
        var indexResult = PackIndex.GeneratePackIndexV2(packFileData);
        var indexEntries = PackIndex.ParsePackIndexV2(indexResult.IndexData);

        // Parse all objects from the pack file
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // Get the commit object
        if (!objectsBySHA1.TryGetValue(commitSha, out var commitObject))
        {
            throw new InvalidOperationException($"Commit {commitSha} not found in pack file");
        }

        if (commitObject.Type != PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {commitSha} is not a commit");
        }

        // Parse the commit to get the tree SHA
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Get all files from the tree recursively
        return GitObjects.GetAllFilesFromTree(commit.TreeSHA1, objectsBySHA1);
    }
}
