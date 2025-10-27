using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace GitCore;

/// <summary>
/// Implements Git Smart HTTP protocol for fetching objects from remote repositories.
/// </summary>
public static class GitSmartHttp
{
    private static readonly HttpClient HttpClient = new();
    
    // Git protocol capabilities we request when fetching pack files
    private const string GitProtocolCapabilities = "multi_ack_detailed side-band-64k ofs-delta";

    /// <summary>
    /// Parses a GitHub or GitLab tree URL to extract repository information and commit SHA.
    /// </summary>
    /// <param name="url">URL like https://github.com/owner/repo/tree/commit-sha</param>
    /// <returns>Tuple of (baseUrl, owner, repo, commitSha)</returns>
    public static (string BaseUrl, string Owner, string Repo, string CommitSha) ParseTreeUrl(string url)
    {
        var uri = new Uri(url);
        var host = uri.Host;
        var scheme = uri.Scheme;
        var pathParts = uri.AbsolutePath.Trim('/').Split('/');

        if (host == "github.com" && pathParts.Length >= 4 && pathParts[2] == "tree")
        {
            // Format: github.com/owner/repo/tree/commit-sha
            return (
                $"{scheme}://{host}",
                pathParts[0],
                pathParts[1],
                pathParts[3]
            );
        }
        else if (host == "gitlab.com" && pathParts.Length >= 5 && pathParts[2] == "-" && pathParts[3] == "tree")
        {
            // Format: gitlab.com/owner/repo/-/tree/commit-sha
            return (
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
    /// <returns>Pack file data</returns>
    public static async Task<ReadOnlyMemory<byte>> FetchPackFileAsync(
        string baseUrl,
        string owner,
        string repo,
        string commitSha)
    {
        var gitUrl = $"{baseUrl}/{owner}/{repo}.git";

        // Step 1: Discover refs (optional but following protocol)
        var refsUrl = $"{gitUrl}/info/refs?service=git-upload-pack";
        
        using var refsRequest = new HttpRequestMessage(HttpMethod.Get, refsUrl);
        using var refsResponse = await HttpClient.SendAsync(refsRequest);
        refsResponse.EnsureSuccessStatusCode();

        // Step 2: Request the pack file with the specific commit
        var uploadPackUrl = $"{gitUrl}/git-upload-pack";
        
        // Build the request body according to Git protocol
        var requestBody = BuildUploadPackRequest(commitSha);
        
        using var packRequest = new HttpRequestMessage(HttpMethod.Post, uploadPackUrl)
        {
            Content = new ByteArrayContent(requestBody)
        };
        packRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-git-upload-pack-request");

        using var packResponse = await HttpClient.SendAsync(packRequest);
        packResponse.EnsureSuccessStatusCode();

        var responseData = await packResponse.Content.ReadAsByteArrayAsync();

        // Parse the response to extract the pack file
        return ExtractPackFileFromResponse(responseData);
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

    private static void WritePktLine(Stream stream, string? line)
    {
        if (line == null)
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
            
            if (lengthHex == "0000")
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
            
            if (sideBand == 0x01)
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
