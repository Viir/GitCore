using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace GitCore;

using FilePath = IReadOnlyList<string>;

/// <summary>
/// Loads Git tree contents from remote URLs.
/// </summary>
public class LoadFromUrl
{
    /// <summary>
    /// Loads the contents of a Git tree from a GitHub or GitLab URL asynchronously.
    /// </summary>
    /// <param name="url">A tree URL like https://github.com/owner/repo/tree/commit-sha or https://github.com/owner/repo/tree/branch</param>
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default static client.</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    public static async Task<IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>>> LoadTreeContentsFromUrlAsync(
        string url,
        HttpClient? httpClient = null)
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
            commitSha =
                await GitSmartHttp.FetchBranchCommitShaAsync(
                parsed.BaseUrl,
                parsed.Owner,
                parsed.Repo,
                parsed.CommitShaOrBranch,
                httpClient);
        }

        // Fetch the pack file containing the commit and its tree
        var packFileData =
            await GitSmartHttp.FetchPackFileAsync(parsed.BaseUrl, parsed.Owner, parsed.Repo, commitSha, httpClient);

        return LoadTreeContentsFromPackFile(packFileData, commitSha);
    }

    /// <summary>
    /// Loads the contents of a Git tree from a GitHub or GitLab URL.
    /// </summary>
    /// <param name="url">A tree URL like https://github.com/owner/repo/tree/commit-sha or https://github.com/owner/repo/tree/branch</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromUrl(string url)
    {
        return LoadTreeContentsFromUrlAsync(url, null).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Loads the contents of a Git tree from a Git repository URL and commit SHA asynchronously.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default static client.</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    public static async Task<IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>>> LoadTreeContentsFromGitUrlAsync(
        string gitUrl,
        string commitSha,
        HttpClient? httpClient = null)
    {
        // Fetch the pack file containing the commit and its tree
        var packFileData =
            await GitSmartHttp.FetchPackFileAsync(gitUrl, commitSha, httpClient);

        return LoadTreeContentsFromPackFile(packFileData, commitSha);
    }

    /// <summary>
    /// Loads the contents of a Git tree from a Git repository URL and commit SHA.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromGitUrl(
        string gitUrl,
        string commitSha)
    {
        return LoadTreeContentsFromGitUrlAsync(gitUrl, commitSha, null).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Loads the contents of a subdirectory within a Git tree from a Git repository URL and commit SHA asynchronously.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <param name="subdirectoryPath">Path to the subdirectory (e.g., ["implement", "GitCore"])</param>
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default static client.</param>
    /// <returns>A dictionary mapping file paths (relative to subdirectory) to their contents</returns>
    public static async Task<IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>>> LoadSubdirectoryContentsFromGitUrlAsync(
        string gitUrl,
        string commitSha,
        FilePath subdirectoryPath,
        HttpClient? httpClient = null)
    {
        // Fetch the pack file containing only objects needed for this subdirectory
        var packFileData =
            await GitSmartHttp.FetchPackFileAsync(gitUrl, commitSha, subdirectoryPath, httpClient);

        return LoadSubdirectoryContentsFromPackFile(packFileData, commitSha, subdirectoryPath);
    }

    /// <summary>
    /// Loads the contents of a subdirectory within a Git tree from a Git repository URL and commit SHA.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <param name="subdirectoryPath">Path to the subdirectory (e.g., ["implement", "GitCore"])</param>
    /// <returns>A dictionary mapping file paths (relative to subdirectory) to their contents</returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadSubdirectoryContentsFromGitUrl(
        string gitUrl,
        string commitSha,
        FilePath subdirectoryPath)
    {
        return LoadSubdirectoryContentsFromGitUrlAsync(gitUrl, commitSha, subdirectoryPath, null).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Loads the contents of a Git tree from pack file data.
    /// </summary>
    /// <param name="packFileData">Pack file data containing the commit and tree objects</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <returns>A dictionary mapping file paths to their contents</returns>
    private static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromPackFile(
        ReadOnlyMemory<byte> packFileData,
        string commitSha)
    {
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
        return GitObjects.GetAllFilesFromTree(
            commit.TreeSHA1,
            sha => objectsBySHA1.TryGetValue(sha, out var obj) ? obj : null);
    }

    /// <summary>
    /// Loads the contents of a subdirectory from pack file data.
    /// </summary>
    /// <param name="packFileData">Pack file data containing the commit and tree objects</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <param name="subdirectoryPath">Path to the subdirectory</param>
    /// <returns>A dictionary mapping file paths (relative to subdirectory) to their contents</returns>
    private static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadSubdirectoryContentsFromPackFile(
        ReadOnlyMemory<byte> packFileData,
        string commitSha,
        FilePath subdirectoryPath)
    {
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

        // Get files from the subdirectory
        return GitObjects.GetFilesFromSubdirectory(
            commit.TreeSHA1,
            subdirectoryPath,
            sha => objectsBySHA1.TryGetValue(sha, out var obj) ? obj : null);
    }

    /// <summary>
    /// Determines if a string is likely a commit SHA (40 hex characters) vs a branch name.
    /// </summary>
    private static bool IsLikelyCommitSha(string value)
    {
        // Git commit SHAs are 40 hex characters
        if (value.Length is not 40)
            return false;

        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }

        return true;
    }
}
