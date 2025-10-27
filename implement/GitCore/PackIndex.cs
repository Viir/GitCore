using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace GitCore;

public static class PackIndex
{
    public record IndexEntry(
        long Offset,
        string SHA1,
        uint CRC32);

    public static IReadOnlyList<IndexEntry> ParsePackIndexV2(ReadOnlyMemory<byte> indexData)
    {
        var span = indexData.Span;

        // Check magic number (0xFF, 't', 'O', 'c')
        ReadOnlySpan<byte> expectedSignature = [0xFF, (byte)'t', (byte)'O', (byte)'c'];
        if (!span[..4].SequenceEqual(expectedSignature))
        {
            throw new ArgumentException("Invalid pack index signature");
        }

        // Check version
        var version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));
        if (version != 2)
        {
            throw new ArgumentException($"Unsupported pack index version: {version}");
        }

        // Read fanout table (256 entries of 4 bytes each = 1024 bytes)
        var fanoutOffset = 8;
        var objectCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(fanoutOffset + 255 * 4, 4));

        // SHA-1 table starts after fanout (256 * 4 bytes)
        var sha1TableOffset = fanoutOffset + 256 * 4;

        // CRC table starts after SHA-1 table (objectCount * 20 bytes)
        var crcTableOffset = sha1TableOffset + (int)objectCount * 20;

        // Offset table starts after CRC table (objectCount * 4 bytes)
        var offsetTableOffset = crcTableOffset + (int)objectCount * 4;

        var entries = new List<IndexEntry>();

        for (var i = 0; i < objectCount; i++)
        {
            // Read SHA-1 (20 bytes)
            var sha1Bytes = span.Slice(sha1TableOffset + i * 20, 20);
            var sha1 = Convert.ToHexStringLower(sha1Bytes);

            // Read CRC32 (4 bytes, big-endian)
            var crc32 = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(crcTableOffset + i * 4, 4));

            // Read offset (4 bytes, big-endian)
            // MSB indicates if this is a 64-bit offset
            var offsetValue = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offsetTableOffset + i * 4, 4));
            long offset;

            if ((offsetValue & 0x80000000) != 0)
            {
                // 64-bit offset - not handling this for now
                throw new NotImplementedException("64-bit offsets not yet supported");
            }
            else
            {
                offset = offsetValue;
            }

            entries.Add(new IndexEntry(offset, sha1, crc32));
        }

        // Sort by offset to make it easier to determine object sizes
        entries.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        return entries;
    }

    public record PackIndexGenerationResult(
        ReadOnlyMemory<byte> IndexData,
        ReadOnlyMemory<byte> ReverseIndexData);

    public static PackIndexGenerationResult GeneratePackIndexV2(ReadOnlyMemory<byte> packFileData)
    {
        // First, create a minimal index by parsing the pack file sequentially
        // to determine object offsets and sizes
        var header = PackFile.ParsePackFileHeader(packFileData);
        var objectCount = (int)header.ObjectCount;
        var packDataWithoutChecksum = packFileData[..^20];
        
        // Parse objects to build the initial list with offsets
        var objects = new List<(long Offset, string SHA1, uint CRC32)>();
        var offset = 12; // After header
        var span = packDataWithoutChecksum.Span;

        for (var i = 0; i < objectCount; i++)
        {
            var startOffset = offset;

            // Parse object header
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

            // Decompress to find the actual compressed length and calculate SHA1
            // We decompress incrementally to find where the compressed data ends
            var compressedLength = FindCompressedLength(span, offset, (int)size);
            var packedSize = (offset - startOffset) + compressedLength;

            // Calculate CRC32 of the complete packed object
            var packedData = span.Slice(startOffset, packedSize);
            var crc32 = CalculateCRC32(packedData);

            // Decompress to calculate SHA1
            var compressedData = span.Slice(offset, compressedLength);
            var decompressed = PackFile.DecompressZlib(compressedData, (int)size);

            // Calculate SHA1
            var objectHeader = System.Text.Encoding.UTF8.GetBytes($"{objectType.ToString().ToLower()} {size}\0");
            var dataForHash = new byte[objectHeader.Length + decompressed.Length];
            Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
            Array.Copy(decompressed, 0, dataForHash, objectHeader.Length, decompressed.Length);
            var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
            var sha1Hex = Convert.ToHexStringLower(sha1);

            objects.Add((startOffset, sha1Hex, crc32));
            offset += compressedLength;
        }

        // Sort objects by SHA1 for the index file
        var sortedObjects = objects.OrderBy(o => o.SHA1, StringComparer.Ordinal).ToList();

        // Build pack index v2
        var indexData = BuildPackIndexV2(sortedObjects, packFileData[^20..]);

        // Build reverse index
        var reverseIndexData = BuildReverseIndexV1(objects, sortedObjects, packFileData[^20..]);

        return new PackIndexGenerationResult(indexData, reverseIndexData);
    }

    private static ReadOnlyMemory<byte> BuildPackIndexV2(
        List<(long Offset, string SHA1, uint CRC32)> sortedObjects,
        ReadOnlyMemory<byte> packChecksum)
    {
        var objectCount = sortedObjects.Count;
        
        // Calculate total size: header(8) + fanout(1024) + sha1s(20*N) + crcs(4*N) + offsets(4*N) + pack_checksum(20) + idx_checksum(20)
        var totalSize = 8 + 1024 + (20 * objectCount) + (4 * objectCount) + (4 * objectCount) + 20 + 20;
        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        // Write signature: 0xFF 't' 'O' 'c'
        span[0] = 0xFF;
        span[1] = (byte)'t';
        span[2] = (byte)'O';
        span[3] = (byte)'c';

        // Write version: 2
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4, 4), 2);

        // Build fanout table
        var fanoutOffset = 8;
        var currentCount = 0;
        for (var i = 0; i < 256; i++)
        {
            // Count how many objects have SHA1 starting with bytes <= i
            while (currentCount < objectCount && Convert.FromHexString(sortedObjects[currentCount].SHA1)[0] <= i)
            {
                currentCount++;
            }
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(fanoutOffset + i * 4, 4), (uint)currentCount);
        }

        // Write SHA1 table
        var sha1Offset = fanoutOffset + 1024;
        for (var i = 0; i < objectCount; i++)
        {
            var sha1Bytes = Convert.FromHexString(sortedObjects[i].SHA1);
            sha1Bytes.CopyTo(span.Slice(sha1Offset + i * 20, 20));
        }

        // Write CRC table
        var crcOffset = sha1Offset + objectCount * 20;
        for (var i = 0; i < objectCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(crcOffset + i * 4, 4), sortedObjects[i].CRC32);
        }

        // Write offset table
        var offsetTableOffset = crcOffset + objectCount * 4;
        for (var i = 0; i < objectCount; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offsetTableOffset + i * 4, 4), (uint)sortedObjects[i].Offset);
        }

        // Write pack checksum
        var packChecksumOffset = offsetTableOffset + objectCount * 4;
        packChecksum.Span.CopyTo(span.Slice(packChecksumOffset, 20));

        // Calculate and write index checksum (SHA1 of everything before this point)
        var dataToHash = span[..(packChecksumOffset + 20)];
        var indexChecksum = System.Security.Cryptography.SHA1.HashData(dataToHash);
        indexChecksum.CopyTo(span.Slice(packChecksumOffset + 20, 20));

        return buffer;
    }

    private static ReadOnlyMemory<byte> BuildReverseIndexV1(
        List<(long Offset, string SHA1, uint CRC32)> objectsInPackOrder,
        List<(long Offset, string SHA1, uint CRC32)> objectsInIndexOrder,
        ReadOnlyMemory<byte> packChecksum)
    {
        var objectCount = objectsInPackOrder.Count;

        // RIDX format:
        // - Header: 'RIDX' (4 bytes) + version (4 bytes) + hash id (4 bytes)
        // - Index array: N entries of 4 bytes each (pack position -> index position)
        // - Checksum: 20 bytes (pack checksum)
        var totalSize = 12 + (4 * objectCount) + 20;
        var buffer = new byte[totalSize];
        var span = buffer.AsSpan();

        // Write signature: 'RIDX'
        span[0] = (byte)'R';
        span[1] = (byte)'I';
        span[2] = (byte)'D';
        span[3] = (byte)'X';

        // Write version: 1
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(4, 4), 1);

        // Write hash id: 1 (SHA-1)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(8, 4), 1);

        // Build reverse index mapping
        // For each position in pack order, find its position in index order
        var indexOffset = 12;
        for (var packPos = 0; packPos < objectCount; packPos++)
        {
            var packObject = objectsInPackOrder[packPos];
            
            // Find this object's position in the sorted (index) order
            var indexPos = objectsInIndexOrder.FindIndex(o => o.SHA1 == packObject.SHA1);
            
            BinaryPrimitives.WriteUInt32BigEndian(span.Slice(indexOffset + packPos * 4, 4), (uint)indexPos);
        }

        // Write pack checksum
        var checksumOffset = indexOffset + objectCount * 4;
        packChecksum.Span.CopyTo(span.Slice(checksumOffset, 20));

        return buffer;
    }

    private static int FindCompressedLength(ReadOnlySpan<byte> packData, int offset, int expectedDecompressedSize)
    {
        // Binary search for the minimum compressed length that allows full decompression
        var minLength = 1;
        var maxLength = packData.Length - offset;
        var workingLength = -1;
        
        // First, find if max length works at all
        if (!TryDecompress(packData, offset, maxLength, expectedDecompressedSize))
        {
            throw new InvalidOperationException($"Cannot decompress object at offset {offset} even with full remaining data");
        }
        
        // Binary search for minimum working length
        while (minLength <= maxLength)
        {
            var mid = minLength + (maxLength - minLength) / 2;
            
            if (TryDecompress(packData, offset, mid, expectedDecompressedSize))
            {
                // This length works, try smaller
                workingLength = mid;
                maxLength = mid - 1;
            }
            else
            {
                // This length doesn't work, need longer
                minLength = mid + 1;
            }
        }
        
        if (workingLength == -1)
        {
            throw new InvalidOperationException($"Could not find compressed length for object at offset {offset}");
        }
        
        return workingLength;
    }
    
    private static bool TryDecompress(ReadOnlySpan<byte> packData, int offset, int compressedLength, int expectedSize)
    {
        try
        {
            var testData = packData.Slice(offset, compressedLength).ToArray();
            using var memStream = new System.IO.MemoryStream(testData);
            using var zlibStream = new System.IO.Compression.ZLibStream(memStream, System.IO.Compression.CompressionMode.Decompress);
            
            var buffer = new byte[expectedSize];
            var totalRead = 0;
            
            while (totalRead < expectedSize)
            {
                var read = zlibStream.Read(buffer, totalRead, expectedSize - totalRead);
                if (read == 0)
                {
                    return false; // Couldn't read enough
                }
                totalRead += read;
            }
            
            return totalRead == expectedSize;
        }
        catch
        {
            return false;
        }
    }

    // Removed ByteTrackingStream class as we're using a simpler approach

    private static uint CalculateCRC32(ReadOnlySpan<byte> data)
    {
        // Standard CRC32 used by Git (polynomial 0x04C11DB7)
        const uint polynomial = 0xEDB88320; // Reversed polynomial
        var table = new uint[256];
        
        // Build CRC table
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            }
            table[i] = crc;
        }

        // Calculate CRC
        uint result = 0xFFFFFFFF;
        foreach (var b in data)
        {
            result = table[(result ^ b) & 0xFF] ^ (result >> 8);
        }
        return ~result;
    }
}
