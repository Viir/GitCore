using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace GitCore;

/// <summary>
/// Implements Git Smart HTTP protocol for fetching objects from remote repositories.
/// </summary>
public static class GitSmartHttp
{
    private static readonly HttpClient s_httpClient = new();

    // Git protocol capabilities we request when fetching pack files
    private const string GitProtocolCapabilities = "multi_ack_detailed side-band-64k ofs-delta";

    /// <summary>
    /// Result of parsing a tree URL.
    /// </summary>
    public record ParseTreeUrlResult(
        string BaseUrl,
        string Owner,
        string Repo,
        string CommitShaOrBranch);

    /// <summary>
    /// Parses a GitHub or GitLab tree URL to extract repository information and commit SHA or branch.
    /// </summary>
    /// <param name="url">URL like https://github.com/owner/repo/tree/commit-sha or https://github.com/owner/repo/tree/main</param>
    /// <returns>Record containing baseUrl, owner, repo, and commitShaOrBranch</returns>
    public static ParseTreeUrlResult ParseTreeUrl(string url)
    {
        var uri = new Uri(url);
        var host = uri.Host;
        var scheme = uri.Scheme;
        var pathParts = uri.AbsolutePath.Trim('/').Split('/');

        if (host is "github.com" && pathParts.Length >= 4 && pathParts[2] is "tree")
        {
            // Format: github.com/owner/repo/tree/commit-sha-or-branch
            return new ParseTreeUrlResult(
                $"{scheme}://{host}",
                pathParts[0],
                pathParts[1],
                pathParts[3]
            );
        }
        else if (host is "gitlab.com" && pathParts.Length >= 5 && pathParts[2] is "-" && pathParts[3] is "tree")
        {
            // Format: gitlab.com/owner/repo/-/tree/commit-sha-or-branch
            return new ParseTreeUrlResult(
                $"{scheme}://{host}",
                pathParts[0],
                pathParts[1],
                pathParts[4]
            );
        }
        else
        {
            throw new ArgumentException($"Unsupported URL format: {url}");
        }
    }

    /// <summary>
    /// Fetches a pack file containing the specified commit and its tree from a remote repository.
    /// </summary>
    /// <param name="baseUrl">Base URL like https://github.com</param>
    /// <param name="owner">Repository owner</param>
    /// <param name="repo">Repository name</param>
    /// <param name="commitSha">Commit SHA to fetch</param>
    /// <param name="httpClient">Optional HttpClient to use for requests. If null, uses a default static client.</param>
    /// <returns>Pack file data</returns>
    public static async Task<ReadOnlyMemory<byte>> FetchPackFileAsync(
        string baseUrl,
        string owner,
        string repo,
        string commitSha,
        HttpClient? httpClient = null)
    {
        var gitUrl = $"{baseUrl}/{owner}/{repo}.git";
        return await FetchPackFileAsync(gitUrl, commitSha, httpClient);
    }

    /// <summary>
    /// Fetches a pack file containing the specified commit and its tree from a remote repository.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to fetch</param>
    /// <param name="httpClient">Optional HttpClient to use for requests. If null, uses a default static client.</param>
    /// <returns>Pack file data</returns>
    public static async Task<ReadOnlyMemory<byte>> FetchPackFileAsync(
        string gitUrl,
        string commitSha,
        HttpClient? httpClient = null)
    {
        return await FetchPackFileAsync(gitUrl, commitSha, subdirectoryPath: null, httpClient);
    }

    /// <summary>
    /// Fetches a pack file containing only objects needed for a specific subdirectory.
    /// </summary>
    /// <param name="gitUrl">Git repository URL like https://github.com/owner/repo.git</param>
    /// <param name="commitSha">Commit SHA to fetch</param>
    /// <param name="subdirectoryPath">Optional subdirectory path to optimize the fetch</param>
    /// <param name="httpClient">Optional HttpClient to use for requests. If null, uses a default static client.</param>
    /// <returns>Pack file data</returns>
    public static async Task<ReadOnlyMemory<byte>> FetchPackFileAsync(
        string gitUrl,
        string commitSha,
        IReadOnlyList<string>? subdirectoryPath,
        HttpClient? httpClient = null)
    {
        httpClient ??= s_httpClient;

        // Ensure the URL ends with .git
        if (!gitUrl.EndsWith(".git"))
        {
            gitUrl = $"{gitUrl}.git";
        }

        // Step 1: Discover refs (optional but following protocol)
        var refsUrl = $"{gitUrl}/info/refs?service=git-upload-pack";

        using var refsRequest = new HttpRequestMessage(HttpMethod.Get, refsUrl);
        using var refsResponse = await httpClient.SendAsync(refsRequest);

        refsResponse.EnsureSuccessStatusCode();

        // Step 2: Request the pack file with the specific commit
        var uploadPackUrl = $"{gitUrl}/git-upload-pack";

        byte[] requestBody;
        
        if (subdirectoryPath != null && subdirectoryPath.Count > 0)
        {
            // For subdirectory optimization, first fetch just the commit and trees to navigate
            // to the subdirectory, then request only the objects we need
            requestBody = BuildUploadPackRequestWithShallow(commitSha);
        }
        else
        {
            // Build the request body according to Git protocol
            requestBody = BuildUploadPackRequest(commitSha);
        }

        using var packRequest = new HttpRequestMessage(HttpMethod.Post, uploadPackUrl)
        {
            Content = new ByteArrayContent(requestBody)
        };

        packRequest.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-git-upload-pack-request");

        using var packResponse = await httpClient.SendAsync(packRequest);

        packResponse.EnsureSuccessStatusCode();

        var responseData = await packResponse.Content.ReadAsByteArrayAsync();

        // Parse the response to extract the pack file
        return ExtractPackFileFromResponse(responseData);
    }

    /// <summary>
    /// Fetches the commit SHA for a given branch from the remote repository.
    /// </summary>
    /// <param name="baseUrl">Base URL like https://github.com</param>
    /// <param name="owner">Repository owner</param>
    /// <param name="repo">Repository name</param>
    /// <param name="branch">Branch name</param>
    /// <param name="httpClient">Optional HttpClient to use for requests. If null, uses a default static client.</param>
    /// <returns>Commit SHA for the branch</returns>
    public static async Task<string> FetchBranchCommitShaAsync(
        string baseUrl,
        string owner,
        string repo,
        string branch,
        HttpClient? httpClient = null)
    {
        httpClient ??= s_httpClient;

        var gitUrl = $"{baseUrl}/{owner}/{repo}.git";
        var refsUrl = $"{gitUrl}/info/refs?service=git-upload-pack";

        using var refsRequest = new HttpRequestMessage(HttpMethod.Get, refsUrl);
        using var refsResponse = await httpClient.SendAsync(refsRequest);
        refsResponse.EnsureSuccessStatusCode();

        var responseData = await refsResponse.Content.ReadAsByteArrayAsync();
        var responseText = Encoding.UTF8.GetString(responseData);

        // Parse pkt-line format to find the ref
        var refName = $"refs/heads/{branch}";
        var lines = responseText.Split('\n');

        foreach (var line in lines)
        {
            if (line.Length > 44 && line.Contains(refName))
            {
                // Extract SHA from the line
                // Format: <4-char-length><40-char-sha><space><ref-name>...
                // Skip the first 4 chars (length prefix)
                var sha = line.Substring(4, 40);
                var rest = line[44..].Trim();

                if (rest.StartsWith(refName))
                {
                    return sha;
                }
            }
        }

        throw new InvalidOperationException($"Branch {branch} not found in repository {owner}/{repo}");
    }

    private static byte[] BuildUploadPackRequest(string commitSha)
    {
        using var ms = new MemoryStream();

        // Want line: want <sha> <capabilities>
        var wantLine = $"want {commitSha} {GitProtocolCapabilities}\n";
        WritePktLine(ms, wantLine);

        // Flush packet
        WritePktLine(ms, null);

        // Done line
        WritePktLine(ms, "done\n");

        return ms.ToArray();
    }

    private static byte[] BuildUploadPackRequestWithShallow(string commitSha)
    {
        using var ms = new MemoryStream();

        // Want line: want <sha> <capabilities> with no-progress and include-tag
        // Using 'shallow' capability to request a shallow clone with depth 1
        var wantLine = $"want {commitSha} {GitProtocolCapabilities} shallow\n";
        WritePktLine(ms, wantLine);

        // Request shallow clone with depth 1 (only this commit, not its history)
        var shallowLine = $"deepen 1\n";
        WritePktLine(ms, shallowLine);

        // Flush packet
        WritePktLine(ms, null);

        // Done line
        WritePktLine(ms, "done\n");

        return ms.ToArray();
    }

    private static void WritePktLine(Stream stream, string? line)
    {
        if (line is null)
        {
            // Flush packet: "0000"
            stream.Write("0000"u8);
        }
        else
        {
            var lineBytes = Encoding.UTF8.GetBytes(line);
            var length = lineBytes.Length + 4; // +4 for the length prefix itself
            var lengthHex = length.ToString("x4");
            stream.Write(Encoding.UTF8.GetBytes(lengthHex));
            stream.Write(lineBytes);
        }
    }

    private static ReadOnlyMemory<byte> ExtractPackFileFromResponse(byte[] responseData)
    {
        // The response is in pkt-line format with side-band
        // Side-band byte: 0x01 = pack data, 0x02 = progress, 0x03 = error

        using var output = new MemoryStream();
        var offset = 0;

        while (offset < responseData.Length)
        {
            // Read pkt-line length (4 hex chars)
            if (offset + 4 > responseData.Length)
                break;

            var lengthHex = Encoding.UTF8.GetString(responseData, offset, 4);
            offset += 4;

            if (lengthHex is "0000")
            {
                // Flush packet, continue
                continue;
            }

            var length = Convert.ToInt32(lengthHex, 16);
            var dataLength = length - 4; // Subtract the 4-byte length prefix

            if (dataLength <= 0 || offset + dataLength > responseData.Length)
                break;

            // First byte is the side-band indicator
            var sideBand = responseData[offset];
            offset++;
            dataLength--;

            if (sideBand is 0x01)
            {
                // Pack data
                output.Write(responseData, offset, dataLength);
            }
            // Ignore progress (0x02) and error (0x03) messages for now

            offset += dataLength;
        }

        return output.ToArray();
    }
}
