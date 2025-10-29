using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default client.</param>
    /// <param name="getBlobFromCache">Optional delegate to retrieve a blob from cache by SHA. Returns null if not in cache.</param>
    /// <param name="reportLoadedBlob">Optional delegate to be invoke when a blob was loaded, with its SHA and content.</param>
    /// <returns>A dictionary mapping file paths (relative to subdirectory) to their contents</returns>
    public static async Task<IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>>> LoadSubdirectoryContentsFromGitUrlAsync(
        string gitUrl,
        string commitSha,
        FilePath subdirectoryPath,
        HttpClient? httpClient = null,
        Func<string, ReadOnlyMemory<byte>?>? getBlobFromCache = null,
        Action<string, ReadOnlyMemory<byte>>? reportLoadedBlob = null)
    {
        return await LoadSubdirectoryContentsWithBloblessCloneAsync(
            gitUrl, commitSha, subdirectoryPath, httpClient, getBlobFromCache, reportLoadedBlob);
    }

    /// <summary>
    /// Loads the contents of a subdirectory within a Git tree from a Git repository URL and commit SHA.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to load</param>
    /// <param name="subdirectoryPath">Path to the subdirectory (e.g., ["implement", "GitCore"])</param>
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default client.</param>
    /// <param name="getBlobFromCache">Optional delegate to retrieve a blob from cache by SHA. Returns null if not in cache.</param>
    /// <param name="reportLoadedBlob">Optional delegate to be invoke when a blob was loaded, with its SHA and content.</param>
    /// <returns>A dictionary mapping file paths (relative to subdirectory) to their contents</returns>
    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadSubdirectoryContentsFromGitUrl(
        string gitUrl,
        string commitSha,
        FilePath subdirectoryPath,
        HttpClient? httpClient = null,
        Func<string, ReadOnlyMemory<byte>?>? getBlobFromCache = null,
        Action<string, ReadOnlyMemory<byte>>? reportLoadedBlob = null)
    {
        return LoadSubdirectoryContentsFromGitUrlAsync(
            gitUrl,
            commitSha,
            subdirectoryPath,
            httpClient,
            getBlobFromCache,
            reportLoadedBlob)
            .GetAwaiter().GetResult();
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
        var (commit, objectsBySHA1) = ParsePackFileAndGetCommit(packFileData, commitSha);

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
        var (commit, objectsBySHA1) = ParsePackFileAndGetCommit(packFileData, commitSha);

        // Get files from the subdirectory
        return GitObjects.GetFilesFromSubdirectory(
            commit.TreeSHA1,
            subdirectoryPath,
            sha => objectsBySHA1.TryGetValue(sha, out var obj) ? obj : null);
    }

    /// <summary>
    /// Common helper to parse pack file and extract commit object.
    /// </summary>
    private static (GitObjects.CommitObject commit, IReadOnlyDictionary<string, PackFile.PackObject> objectsBySHA1)
        ParsePackFileAndGetCommit(ReadOnlyMemory<byte> packFileData, string commitSha)
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

        return (commit, objectsBySHA1);
    }

    /// <summary>
    /// Loads subdirectory contents using blobless clone optimization.
    /// First fetches only trees and commit, then requests specific blobs for the subdirectory.
    /// </summary>
    private static async Task<IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>>> LoadSubdirectoryContentsWithBloblessCloneAsync(
        string gitUrl,
        string commitSha,
        FilePath subdirectoryPath,
        HttpClient? httpClient,
        Func<string, ReadOnlyMemory<byte>?>? getBlobFromCache,
        Action<string, ReadOnlyMemory<byte>>? reportLoadedBlob)
    {
        // Step 1: Fetch blobless pack file (commit and trees only)
        var bloblessPackFileData =
            await GitSmartHttp.FetchBloblessPackFileAsync(gitUrl, commitSha, depth: 1, httpClient);

        // Parse the blobless pack file
        var indexResult = PackIndex.GeneratePackIndexV2(bloblessPackFileData);
        var indexEntries = PackIndex.ParsePackIndexV2(indexResult.IndexData);
        var objects = PackFile.ParseAllObjects(bloblessPackFileData, indexEntries);
        var objectsBySHA1 = new Dictionary<string, PackFile.PackObject>(PackFile.GetObjectsBySHA1(objects));

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

        // Step 2: Navigate trees to find blob SHAs in the subdirectory
        var blobShas = new List<string>();
        CollectBlobShasFromSubdirectory(
            commit.TreeSHA1,
            subdirectoryPath,
            sha => objectsBySHA1.TryGetValue(sha, out var obj) ? obj : null,
            blobShas);

        // Step 3: Check cache for blobs we already have
        var cachedBlobs = new Dictionary<string, ReadOnlyMemory<byte>>();
        var missingBlobShas = new List<string>();

        if (getBlobFromCache is not null)
        {
            foreach (var blobSha in blobShas)
            {
                if (getBlobFromCache(blobSha) is { } cached)
                {
                    cachedBlobs[blobSha] = cached;
                }
                else
                {
                    missingBlobShas.Add(blobSha);
                }
            }
        }
        else
        {
            missingBlobShas.AddRange(blobShas);
        }

        // Step 4: Fetch missing blobs
        if (missingBlobShas.Count > 0)
        {
            var blobsPackFileData =
                await GitSmartHttp.FetchSpecificObjectsAsync(gitUrl, missingBlobShas, httpClient);

            // Parse the blobs pack file
            var blobsIndexResult = PackIndex.GeneratePackIndexV2(blobsPackFileData);
            var blobsIndexEntries = PackIndex.ParsePackIndexV2(blobsIndexResult.IndexData);
            var blobObjects = PackFile.ParseAllObjects(blobsPackFileData, blobsIndexEntries);

            foreach (var blobObject in blobObjects)
            {
                if (blobObject.Type is PackFile.ObjectType.Blob)
                {
                    cachedBlobs[blobObject.SHA1base16] = blobObject.Data;
                }

                // Support caller caching blobs for future reads.
                reportLoadedBlob?.Invoke(blobObject.SHA1base16, blobObject.Data);
            }
        }

        // Step 5: Build the final dictionary with all objects (trees from step 1 + blobs from steps 3&4)
        foreach (var (sha, blob) in cachedBlobs)
        {
            if (!objectsBySHA1.ContainsKey(sha))
            {
                objectsBySHA1[sha] =
                    new PackFile.PackObject(
                        PackFile.ObjectType.Blob,
                        blob.Length,
                        blob,
                        sha);
            }
        }

        // Step 6: Get files from the subdirectory (now we have all the blobs)
        return GitObjects.GetFilesFromSubdirectory(
            commit.TreeSHA1,
            subdirectoryPath,
            sha => objectsBySHA1.TryGetValue(sha, out var obj) ? obj : null);
    }

    /// <summary>
    /// Collects blob SHAs from a subdirectory by navigating the tree structure.
    /// </summary>
    private static void CollectBlobShasFromSubdirectory(
        string treeSHA1,
        FilePath subdirectoryPath,
        Func<string, PackFile.PackObject?> getObjectBySHA1,
        List<string> blobShas)
    {
        // Navigate to the subdirectory
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

            var tree = GitObjects.ParseTree(treeObject.Data);
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

        // Now collect all blob SHAs from this tree recursively
        CollectBlobShasFromTree(currentTreeSHA1, getObjectBySHA1, blobShas);
    }

    /// <summary>
    /// Recursively collects all blob SHAs from a tree.
    /// </summary>
    private static void CollectBlobShasFromTree(
        string treeSHA1,
        Func<string, PackFile.PackObject?> getObjectBySHA1,
        List<string> blobShas)
    {
        var treeObject = getObjectBySHA1(treeSHA1);
        if (treeObject is null)
        {
            throw new InvalidOperationException($"Tree {treeSHA1} not found");
        }

        if (treeObject.Type is not PackFile.ObjectType.Tree)
        {
            throw new InvalidOperationException($"Object {treeSHA1} is not a tree");
        }

        var tree = GitObjects.ParseTree(treeObject.Data);

        foreach (var entry in tree.Entries)
        {
            if (entry.Mode is "40000") // Directory
            {
                CollectBlobShasFromTree(entry.SHA1, getObjectBySHA1, blobShas);
            }
            else // File (blob)
            {
                blobShas.Add(entry.SHA1);
            }
        }
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

    /// <summary>
    /// Result of a blobless clone containing Git objects (commits and trees) indexed by SHA.
    /// </summary>
    public record BloblessCloneResult(
        string CommitSha,
        IReadOnlyDictionary<string, PackFile.PackObject> ObjectsBySha);

    /// <summary>
    /// Fetches a blobless clone (commits and trees only, no blobs) from a Git repository.
    /// This allows consumers to navigate the tree structure themselves without fetching file contents.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to fetch</param>
    /// <param name="depth">Clone depth to control how many commits to fetch. Default is 1 (only the specified commit).</param>
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default static client.</param>
    /// <returns>A result containing the commit SHA and a dictionary of objects indexed by their SHA</returns>
    public static async Task<BloblessCloneResult> FetchBloblessCloneAsync(
        string gitUrl,
        string commitSha,
        int depth = 1,
        HttpClient? httpClient = null)
    {
        // Fetch blobless pack file (commit and trees only)
        var bloblessPackFileData =
            await GitSmartHttp.FetchBloblessPackFileAsync(gitUrl, commitSha, depth, httpClient);

        // Parse the blobless pack file
        var indexResult = PackIndex.GeneratePackIndexV2(bloblessPackFileData);
        var indexEntries = PackIndex.ParsePackIndexV2(indexResult.IndexData);
        var objects = PackFile.ParseAllObjects(bloblessPackFileData, indexEntries);
        var objectsBySha = PackFile.GetObjectsBySHA1(objects);

        return new BloblessCloneResult(commitSha, objectsBySha);
    }

    /// <summary>
    /// Fetches a blobless clone (commits and trees only, no blobs) from a Git repository.
    /// This allows consumers to navigate the tree structure themselves without fetching file contents.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to fetch</param>
    /// <param name="depth">Clone depth to control how many commits to fetch. Default is 1 (only the specified commit).</param>
    /// <param name="httpClient">Optional HttpClient to use for HTTP requests. If null, uses a default static client.</param>
    /// <returns>A result containing the commit SHA and a dictionary of objects indexed by their SHA</returns>
    public static BloblessCloneResult FetchBloblessClone(
        string gitUrl,
        string commitSha,
        int depth = 1,
        HttpClient? httpClient = null)
    {
        return FetchBloblessCloneAsync(gitUrl, commitSha, depth, httpClient).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Navigates to a subtree within a tree structure given a path.
    /// </summary>
    /// <param name="treeSha">The SHA of the root tree to start navigation from</param>
    /// <param name="path">Path components to navigate (e.g., ["implement", "GitCore"])</param>
    /// <param name="objectsBySha">Dictionary of objects indexed by SHA</param>
    /// <returns>The SHA of the subtree at the specified path</returns>
    /// <exception cref="InvalidOperationException">Thrown if the path cannot be navigated</exception>
    public static string NavigateToSubtree(
        string treeSha,
        FilePath path,
        IReadOnlyDictionary<string, PackFile.PackObject> objectsBySha)
    {
        var currentTreeSha = treeSha;

        foreach (var pathComponent in path)
        {
            if (!objectsBySha.TryGetValue(currentTreeSha, out var treeObject))
            {
                throw new InvalidOperationException($"Tree {currentTreeSha} not found");
            }

            if (treeObject.Type is not PackFile.ObjectType.Tree)
            {
                throw new InvalidOperationException($"Object {currentTreeSha} is not a tree");
            }

            var tree = GitObjects.ParseTree(treeObject.Data);
            var entry = tree.Entries.FirstOrDefault(e => e.Name == pathComponent);

            if (entry is null)
            {
                throw new InvalidOperationException($"Path component '{pathComponent}' not found in tree");
            }

            if (entry.Mode is not "40000")
            {
                throw new InvalidOperationException($"Path component '{pathComponent}' is not a directory");
            }

            currentTreeSha = entry.SHA1;
        }

        return currentTreeSha;
    }

    /// <summary>
    /// Gets all entries (files and directories) from a tree.
    /// </summary>
    /// <param name="treeSha">The SHA of the tree to list</param>
    /// <param name="objectsBySha">Dictionary of objects indexed by SHA</param>
    /// <returns>A list of tree entries</returns>
    /// <exception cref="InvalidOperationException">Thrown if the tree cannot be found or parsed</exception>
    public static IReadOnlyList<GitObjects.TreeEntry> GetTreeEntries(
        string treeSha,
        IReadOnlyDictionary<string, PackFile.PackObject> objectsBySha)
    {
        if (!objectsBySha.TryGetValue(treeSha, out var treeObject))
        {
            throw new InvalidOperationException($"Tree {treeSha} not found");
        }

        if (treeObject.Type is not PackFile.ObjectType.Tree)
        {
            throw new InvalidOperationException($"Object {treeSha} is not a tree");
        }

        var tree = GitObjects.ParseTree(treeObject.Data);
        return tree.Entries;
    }
}
