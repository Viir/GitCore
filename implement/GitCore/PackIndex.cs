using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace GitCore;

/// <summary>
/// Utilities for reading and generating Git pack index files and reverse index files.
/// </summary>
public static class PackIndex
{
    /// <summary>
    /// Represents a single entry in a pack index file.
    /// </summary>
    /// <param name="Offset">The byte offset of the packed object within the pack file.</param>
    /// <param name="SHA1">The object identifier as a40-character lowercase hexadecimal SHA-1 string.</param>
    /// <param name="CRC32">The CRC-32 checksum of the packed object data as stored in the pack.</param>
    public record IndexEntry(
        long Offset,
        string SHA1,
        uint CRC32);

    /// <summary>
    /// Parses a Git pack index version2 (idx) file from the provided data.
    /// </summary>
    /// <param name="indexData">The bytes of the idx file.</param>
    /// <returns>
    /// A read-only list of <see cref="IndexEntry"/> values, sorted by increasing <see cref="IndexEntry.Offset"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the signature is invalid or the version is not2.
    /// </exception>
    /// <exception cref="NotImplementedException">
    /// Thrown when the index references64-bit object offsets which are not supported by this parser yet.
    /// </exception>
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

    /// <summary>
    /// The result of generating pack index and reverse index content for a pack file.
    /// </summary>
    /// <param name="IndexData">The bytes of the generated idx (version2) file.</param>
    /// <param name="ReverseIndexData">The bytes of the generated reverse index (RIDX) file.</param>
    public record PackIndexGenerationResult(
        ReadOnlyMemory<byte> IndexData,
        ReadOnlyMemory<byte> ReverseIndexData);

    /// <summary>
    /// Generates a pack index (idx v2) and a reverse index (RIDX v1) from a pack file.
    /// </summary>
    /// <param name="packFileData">The bytes of the pack file including its trailing20-byte checksum.</param>
    /// <returns>
    /// A <see cref="PackIndexGenerationResult"/> containing the idx and RIDX file contents.
    /// </returns>
    /// <remarks>
    /// This implementation emits only32-bit offsets in the idx file and does not include
    /// the large-offsets table; therefore, it is suitable only for packs whose object offsets
    /// fit within32 bits.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when object decompression fails or the actual decompressed size does not match the header.
    /// </exception>
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
        // - Pack checksum: 20 bytes
        // - Rev checksum: 20 bytes (SHA-1 of everything before this point)
        var totalSize = 12 + (4 * objectCount) + 20 + 20;
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
        var packChecksumOffset = indexOffset + objectCount * 4;
        packChecksum.Span.CopyTo(span.Slice(packChecksumOffset, 20));

        // Calculate and write rev checksum (SHA-1 of everything before this point)
        var revChecksumOffset = packChecksumOffset + 20;
        var dataToHash = span[..revChecksumOffset];
        var revChecksum = System.Security.Cryptography.SHA1.HashData(dataToHash);
        revChecksum.CopyTo(span.Slice(revChecksumOffset, 20));

        return buffer;
    }

    private static int FindCompressedLength(ReadOnlySpan<byte> packData, int offset, int expectedDecompressedSize)
    {
        // Use SharpZipLib's Inflater which tracks bytes consumed via TotalIn property
        var inflater = new Inflater(false); // false = expect zlib header

        try
        {
            // Provide all available data to the inflater
            var availableData = packData[offset..].ToArray();
            inflater.SetInput(availableData);

            // Decompress to a buffer - use a slightly larger buffer to ensure the inflater
            // reads the entire compressed stream and sets IsFinished
            var outputBuffer = new byte[expectedDecompressedSize + 1];
            var decompressedBytes = inflater.Inflate(outputBuffer);

            if (decompressedBytes != expectedDecompressedSize)
            {
                throw new InvalidOperationException(
                $"Decompression produced {decompressedBytes} bytes but expected {expectedDecompressedSize} bytes at offset {offset}");
            }

            // TotalIn tells us exactly how many bytes were consumed from the input
            // This should now be accurate since the inflater has finished the stream
            return (int)inflater.TotalIn;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not decompress object at offset {offset}: {ex.Message}", ex);
        }
        finally
        {
            inflater.Reset();
        }
    }

    // Removed ByteTrackingStream class as we're using a simpler approach

    private static uint CalculateCRC32(ReadOnlySpan<byte> data)
    {
        // Standard CRC32 used by Git (polynomial 0x04C11DB7)
        const uint Polynomial = 0xEDB88320; // Reversed polynomial
        var table = new uint[256];

        // Build CRC table
        for (uint i = 0; i < 256; i++)
        {
            var crc = i;

            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) is not 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[i] = crc;
        }

        // Calculate CRC
        var result = 0xFFFFFFFF;

        foreach (var b in data)
        {
            result = table[(result ^ b) & 0xFF] ^ (result >> 8);
        }

        return ~result;
    }
}
