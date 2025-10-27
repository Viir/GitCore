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
    /// <param name="url">A tree URL like https://github.com/owner/repo/tree/commit-sha or https://github.com/owner/repo/tree/branch</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromUrl(string url)
    {
        // Parse the URL to extract repository information and commit SHA or branch
        var parsed = GitSmartHttp.ParseTreeUrl(url);

        // Determine if it's a commit SHA or branch name
        string commitSha;
        if (IsLikelyCommitSha(parsed.CommitShaOrBranch))
        {
            commitSha = parsed.CommitShaOrBranch;
        }
        else
        {
            // It's a branch name, resolve it to a commit SHA
            commitSha = GitSmartHttp.FetchBranchCommitShaAsync(
                parsed.BaseUrl, 
                parsed.Owner, 
                parsed.Repo, 
                parsed.CommitShaOrBranch)
                .GetAwaiter()
                .GetResult();
        }

        // Fetch the pack file containing the commit and its tree
        var packFileData = GitSmartHttp.FetchPackFileAsync(parsed.BaseUrl, parsed.Owner, parsed.Repo, commitSha)
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

        if (commitObject.Type is not PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {commitSha} is not a commit");
        }

        // Parse the commit to get the tree SHA
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Get all files from the tree recursively
        return GitObjects.GetAllFilesFromTree(commit.TreeSHA1, objectsBySHA1);
    }

    /// <summary>
    /// Determines if a string is likely a commit SHA (40 hex characters) vs a branch name.
    /// </summary>
    private static bool IsLikelyCommitSha(string value)
    {
        // Git commit SHAs are 40 hex characters
        if (value.Length != 40)
            return false;

        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }

        return true;
    }
}
