using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace GitCore;

using FilePath = IReadOnlyList<string>;

public class LoadFromUrl
{
    public record GitUrlInfo(
        string Host,
        string Owner,
        string Repo,
        string CommitSha);

    public static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTreeContentsFromUrl(string url)
    {
        var urlInfo = ParseGitUrl(url);
        
        // Fetch pack file from remote repository
        var packFileData = FetchPackFileAsync(urlInfo).GetAwaiter().GetResult();
        
        // Build index for pack file
        var indexData = BuildPackIndex(packFileData);
        
        // Use existing pack file parser with the generated index
        var indexEntries = PackIndex.ParsePackIndexV2(indexData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);
        
        // Get the commit object
        if (!objectsBySHA1.TryGetValue(urlInfo.CommitSha, out var commitObject))
        {
            throw new InvalidOperationException($"Commit {urlInfo.CommitSha} not found in pack file");
        }
        
        if (commitObject.Type != PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {urlInfo.CommitSha} is not a commit");
        }
        
        var commit = GitObjects.ParseCommit(commitObject.Data);
        
        // Get tree contents
        return GetTreeContentsRecursive(commit.TreeSHA1, objectsBySHA1, []);
    }

    private static ReadOnlyMemory<byte> BuildPackIndex(ReadOnlyMemory<byte> packFileData)
    {
        // Build a Git pack index v2 file from a pack file
        var objects = ScanPackFileForIndex(packFileData);
        
        // Build index file
        using var indexStream = new MemoryStream();
        
        // Write signature and version
        indexStream.Write([0xFF, (byte)'t', (byte)'O', (byte)'c']);
        indexStream.Write(BitConverter.IsLittleEndian ? 
            BitConverter.GetBytes(2u).Reverse().ToArray() : BitConverter.GetBytes(2u));
        
        // Sort objects by SHA1 for fanout table
        var sortedObjects = objects.OrderBy(o => o.sha1).ToList();
        
        // Build fanout table
        var fanout = new uint[256];
        foreach (var obj in sortedObjects)
        {
            var firstByte = Convert.FromHexString(obj.sha1.Substring(0, 2))[0];
            for (var i = firstByte; i < 256; i++)
            {
                fanout[i]++;
            }
        }
        
        // Write fanout table
        foreach (var count in fanout)
        {
            var bytes = BitConverter.IsLittleEndian ? 
                BitConverter.GetBytes(count).Reverse().ToArray() : BitConverter.GetBytes(count);
            indexStream.Write(bytes);
        }
        
        // Write SHA1 table
        foreach (var obj in sortedObjects)
        {
            indexStream.Write(Convert.FromHexString(obj.sha1));
        }
        
        // Write CRC table (zeros for simplicity)
        for (var i = 0; i < sortedObjects.Count; i++)
        {
            indexStream.Write(new byte[4]);
        }
        
        // Write offset table (need to find objects by SHA1 in original order)
        var offsetBySHA1 = objects.ToDictionary(o => o.sha1, o => o.offset);
        foreach (var obj in sortedObjects)
        {
            var offset = (uint)offsetBySHA1[obj.sha1];
            var bytes = BitConverter.IsLittleEndian ?
                BitConverter.GetBytes(offset).Reverse().ToArray() : BitConverter.GetBytes(offset);
            indexStream.Write(bytes);
        }
        
        // Write pack checksum
        var packChecksum = packFileData[^20..];
        indexStream.Write(packChecksum.Span);
        
        return indexStream.ToArray();
    }

    private static List<(long offset, string sha1)> ScanPackFileForIndex(ReadOnlyMemory<byte> packFileData)
    {
        var span = packFileData.Span;
        var objectCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        
        Console.WriteLine($"Scanning pack file with {objectCount} objects");
        
        var objects = new List<(long offset, string sha1)>();
        var offset = 12;
        var dataWithoutChecksum = packFileData[..^20];
        
        for (var i = 0; i < objectCount; i++)
        {
            var objectOffset = offset;
            
            Console.WriteLine($"Object {i + 1} at offset {offset}");
            
            // Read object header
            var currentByte = span[offset++];
            var objectType = (PackFile.ObjectType)((currentByte >> 4) & 0x7);
            long size = currentByte & 0xF;
            var shift = 4;
            
            while ((currentByte & 0x80) != 0)
            {
                currentByte = span[offset++];
                size |= (long)(currentByte & 0x7F) << shift;
                shift += 7;
            }
            
            Console.WriteLine($"  Type: {objectType}, Size: {size}");
            
            // Find compressed data size by trial decompression
            var compressedStart = offset;
            var compressedSize = FindCompressedSize(span[compressedStart..dataWithoutChecksum.Length], (int)size);
            
            Console.WriteLine($"  Compressed size: {compressedSize}");
            
            // Decompress to get SHA1
            var decompressed = DecompressZlib(span[compressedStart..(compressedStart + compressedSize)], (int)size);
            
            // Calculate SHA1
            var objectHeader = Encoding.UTF8.GetBytes($"{objectType.ToString().ToLower()} {decompressed.Length}\0");
            var dataForHash = new byte[objectHeader.Length + decompressed.Length];
            Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
            Array.Copy(decompressed, 0, dataForHash, objectHeader.Length, decompressed.Length);
            var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
            var sha1Hex = Convert.ToHexStringLower(sha1);
            
            Console.WriteLine($"  SHA1: {sha1Hex}");
            
            objects.Add((objectOffset, sha1Hex));
            
            offset = compressedStart + compressedSize;
        }
        
        return objects;
    }

    private static int FindCompressedSize(ReadOnlySpan<byte> data, int expectedDecompressedSize)
    {
        // Try increasing sizes until we can decompress exactly expectedDecompressedSize bytes
        for (var size = 1; size <= Math.Min(data.Length, 10000); size++) // Limit to avoid timeout
        {
            try
            {
                var decompressed = DecompressZlib(data[..size], expectedDecompressedSize);
                if (decompressed.Length == expectedDecompressedSize)
                {
                    return size;
                }
            }
            catch (Exception)
            {
                continue;
            }
        }
        
        // If we still haven't found it, the expected size might be in a delta object
        // For now, throw an error
        throw new InvalidOperationException($"Could not find compressed size for {expectedDecompressedSize} bytes (searched {Math.Min(data.Length, 10000)} sizes)");
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressed, int expectedSize)
    {
        using var ms = new MemoryStream(compressed.ToArray());
        using var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
        
        var result = new byte[expectedSize];
        var totalRead = 0;
        
        while (totalRead < expectedSize)
        {
            var bytesRead = zlib.Read(result, totalRead, expectedSize - totalRead);
            if (bytesRead == 0)
            {
                // Reached end of stream before reading expectedSize bytes
                // Return what we have
                return result[..totalRead].ToArray();
            }
            totalRead += bytesRead;
        }
        
        return result;
    }

    private static GitUrlInfo ParseGitUrl(string url)
    {
        // Parse URLs like: https://github.com/Viir/GitCore/tree/14eb05f5beac67cdf2a229394baa626338a3d92e
        // or: https://gitlab.com/owner/repo/tree/commitsha
        
        var uri = new Uri(url);
        var host = uri.Host;
        var pathSegments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (pathSegments.Length < 4)
        {
            throw new ArgumentException($"Invalid Git URL format: {url}");
        }
        
        var owner = pathSegments[0];
        var repo = pathSegments[1];
        var commitSha = pathSegments[3];
        
        return new GitUrlInfo(host, owner, repo, commitSha);
    }

    private static async Task<ReadOnlyMemory<byte>> FetchPackFileAsync(GitUrlInfo urlInfo)
    {
        using var client = new HttpClient();
        
        // Construct the Git URL for upload-pack
        var gitUrl = $"https://{urlInfo.Host}/{urlInfo.Owner}/{urlInfo.Repo}.git";
        
        // First, discover references to get the capabilities
        var infoRefsUrl = $"{gitUrl}/info/refs?service=git-upload-pack";
        var infoRefsResponse = await client.GetAsync(infoRefsUrl);
        infoRefsResponse.EnsureSuccessStatusCode();
        
        var infoRefsData = await infoRefsResponse.Content.ReadAsByteArrayAsync();
        
        // Now request the specific commit
        var uploadPackUrl = $"{gitUrl}/git-upload-pack";
        var requestBody = BuildUploadPackRequest(urlInfo.CommitSha);
        
        var content = new ByteArrayContent(requestBody);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-git-upload-pack-request");
        
        var uploadPackResponse = await client.PostAsync(uploadPackUrl, content);
        uploadPackResponse.EnsureSuccessStatusCode();
        
        var responseData = await uploadPackResponse.Content.ReadAsByteArrayAsync();
        
        // Parse the response to extract pack file
        var packFileData = ParseUploadPackResponse(responseData);
        
        return packFileData;
    }

    private static byte[] BuildUploadPackRequest(string commitSha)
    {
        using var ms = new MemoryStream();
        
        // Write want line with minimal capabilities
        var wantLine = $"want {commitSha}\n";
        WritePktLine(ms, wantLine);
        
        // Write flush packet to indicate end of wants
        WritePktLine(ms, null);
        
        // Write done line to indicate we're ready for pack
        WritePktLine(ms, "done\n");
        
        return ms.ToArray();
    }

    private static void WritePktLine(Stream stream, string? line)
    {
        if (line == null)
        {
            // Flush packet
            stream.Write(Encoding.ASCII.GetBytes("0000"));
        }
        else
        {
            var data = Encoding.UTF8.GetBytes(line);
            var length = data.Length + 4; // +4 for the length prefix itself
            var lengthHex = length.ToString("x4");
            stream.Write(Encoding.ASCII.GetBytes(lengthHex));
            stream.Write(data);
        }
    }

    private static ReadOnlyMemory<byte> ParseUploadPackResponse(byte[] responseData)
    {
        // Parse pkt-line format and extract pack data
        var offset = 0;
        var span = responseData.AsSpan();
        using var packStream = new MemoryStream();
        
        
        while (offset < span.Length)
        {
            // Check if we've reached raw PACK data (not in pkt-line format)
            if (offset + 4 <= span.Length && 
                span[offset] == 'P' && span[offset + 1] == 'A' && 
                span[offset + 2] == 'C' && span[offset + 3] == 'K')
            {
                // Found raw PACK data
                packStream.Write(span[offset..]);
                break;
            }
            
            // Try to read pkt-line length
            if (offset + 4 > span.Length)
            {
                break;
            }
            
            var lengthHex = Encoding.ASCII.GetString(span.Slice(offset, 4));
            if (!int.TryParse(lengthHex, System.Globalization.NumberStyles.HexNumber, null, out var length))
            {
                // Not a valid pkt-line, skip forward
                offset++;
                continue;
            }
            
            
            if (length == 0)
            {
                // Flush packet
                offset += 4;
                continue;
            }
            
            if (length < 4 || offset + length > span.Length)
            {
                // Invalid length, skip
                offset++;
                continue;
            }
            
            // Read the pkt-line data (excluding the 4-byte length prefix)
            var pktData = span.Slice(offset + 4, length - 4);
            
            // The response may not use side-band, just check if it's PACK data directly
            if (pktData.Length >= 4 && 
                pktData[0] == 'P' && pktData[1] == 'A' && 
                pktData[2] == 'C' && pktData[3] == 'K')
            {
                packStream.Write(pktData);
                // Continue reading more pkt-lines that contain pack data
            }
            else if (pktData.Length > 0)
            {
                // Could be NAK or other response
                var text = Encoding.UTF8.GetString(pktData);
                if (text.StartsWith("NAK"))
                {
                }
            }
            
            offset += length;
        }
        
        var packBytes = packStream.ToArray();
        if (packBytes.Length == 0)
        {
            throw new InvalidOperationException("Could not find PACK data in response");
        }
        
        return packBytes;
    }

    private static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> GetTreeContentsRecursive(
        string treeSHA1,
        IReadOnlyDictionary<string, PackFile.PackObject> objectsBySHA1,
        FilePath currentPath)
    {
        var result = new Dictionary<FilePath, ReadOnlyMemory<byte>>(
            comparer: Common.EnumerableExtensions.EqualityComparer<FilePath>());
        
        if (!objectsBySHA1.TryGetValue(treeSHA1, out var treeObject))
        {
            throw new InvalidOperationException($"Tree {treeSHA1} not found in pack file");
        }
        
        if (treeObject.Type != PackFile.ObjectType.Tree)
        {
            throw new InvalidOperationException($"Object {treeSHA1} is not a tree");
        }
        
        var tree = GitObjects.ParseTree(treeObject.Data);
        
        foreach (var entry in tree.Entries)
        {
            var entryPath = currentPath.Append(entry.Name).ToList();
            
            if (entry.Mode.StartsWith("100")) // Regular file
            {
                if (objectsBySHA1.TryGetValue(entry.SHA1, out var blobObject))
                {
                    if (blobObject.Type == PackFile.ObjectType.Blob)
                    {
                        result[entryPath] = GitObjects.GetBlobContent(blobObject.Data);
                    }
                }
            }
            else if (entry.Mode == "40000") // Directory
            {
                // Recursively process subdirectory
                var subTreeContents = GetTreeContentsRecursive(entry.SHA1, objectsBySHA1, entryPath);
                foreach (var kvp in subTreeContents)
                {
                    result[kvp.Key] = kvp.Value;
                }
            }
        }
        
        return result;
    }
}
