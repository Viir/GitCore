using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

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
    /// <param name="SHA1base16">The object identifier as a40-character lowercase hexadecimal SHA-1 string.</param>
    /// <param name="CRC32">The CRC-32 checksum of the packed object data as stored in the pack.</param>
    public record IndexEntry(
        long Offset,
        string SHA1base16,
        uint CRC32);

    /// <summary>
    /// Parses a Git pack index version2 (idx) file from the provided data.
    /// </summary>
    /// <param name="indexData">The bytes of the idx file.</param>
    /// <returns>
    /// A read-only list of <see cref="IndexEntry"/> values, sorted by increasing <see cref="IndexEntry.Offset"/>.
    /// </returns>
    /// <exception cref="InvalidPackIndexException">Thrown when the index is malformed or unsupported.</exception>
    public static IReadOnlyList<IndexEntry> ParsePackIndexV2(ReadOnlyMemory<byte> indexData)
    {
        return ParsePackIndexV2(indexData, indexPath: null);
    }

    /// <summary>
    /// Parses a Git pack index version 2 file and associates failures with its storage path.
    /// </summary>
    public static IReadOnlyList<IndexEntry> ParsePackIndexV2(
        ReadOnlyMemory<byte> indexData,
        string? indexPath)
    {
        var indexLength = indexData.Length;

        uint? detectedVersion =
            indexLength >= 8
            ?
            BinaryPrimitives.ReadUInt32BigEndian(indexData.Span.Slice(4, 4))
            :
            null;

        var span = indexData.Span;

        InvalidPackIndexException Invalid(string message, Exception? innerException = null)
        {
            return
                new InvalidPackIndexException(
                    message,
                    new GitErrorContext(
                        "ParsePackIndexV2",
                        IndexPath: indexPath,
                        IndexVersion: detectedVersion,
                        ObservedValue: indexLength),
                    innerException);
        }

        try
        {
            const int HeaderAndFanoutLength = 8 + (256 * 4);
            const int TrailerLength = 40;

            if (span.Length < HeaderAndFanoutLength + TrailerLength)
                throw Invalid($"Pack index is truncated ({span.Length} bytes).");

            ReadOnlySpan<byte> expectedSignature = [0xFF, (byte)'t', (byte)'O', (byte)'c'];

            if (!span[..4].SequenceEqual(expectedSignature))
                throw Invalid("Invalid pack index signature.");

            var version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));

            if (version is not 2)
                throw Invalid($"Unsupported pack index version: {version}.");

            var fanoutOffset = 8;
            uint previousFanout = 0;

            for (var index = 0; index < 256; index++)
            {
                var fanout =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        span.Slice(fanoutOffset + index * 4, 4));

                if (fanout < previousFanout)
                    throw Invalid($"Pack index fanout table decreases at entry {index}.");

                previousFanout = fanout;
            }

            var objectCountValue = previousFanout;

            if (objectCountValue > int.MaxValue)
                throw Invalid($"Pack index object count {objectCountValue} exceeds the supported limit.");

            var objectCount = (int)objectCountValue;
            var sha1TableOffset = (long)HeaderAndFanoutLength;
            var crcTableOffset = checked(sha1TableOffset + objectCount * 20L);
            var offsetTableOffset = checked(crcTableOffset + objectCount * 4L);
            var largeOffsetTableOffset = checked(offsetTableOffset + objectCount * 4L);

            if (largeOffsetTableOffset + TrailerLength > span.Length)
                throw Invalid("Pack index tables extend beyond the available data.");

            var largeOffsetCount = 0;

            for (var index = 0; index < objectCount; index++)
            {
                var offsetValue =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        span.Slice(checked((int)(offsetTableOffset + index * 4L)), 4));

                if ((offsetValue & 0x80000000u) is not 0)
                {
                    var referencedIndex = offsetValue & 0x7FFFFFFFu;

                    if (referencedIndex != largeOffsetCount)
                    {
                        throw Invalid(
                            $"Large-offset table index {referencedIndex} is inconsistent; " +
                            $"expected {largeOffsetCount}.");
                    }

                    largeOffsetCount++;
                }
            }

            var expectedLength =
                checked(largeOffsetTableOffset + largeOffsetCount * 8L + TrailerLength);

            if (expectedLength != span.Length)
            {
                throw Invalid(
                    $"Pack index length is {span.Length}, but its tables require {expectedLength} bytes.");
            }

            var calculatedChecksum = System.Security.Cryptography.SHA1.HashData(span[..^20]);

            if (!span[^20..].SequenceEqual(calculatedChecksum))
                throw Invalid("Pack index checksum does not match its content.");

            var entries = new List<IndexEntry>(objectCount);

            for (var index = 0; index < objectCount; index++)
            {
                var sha1Bytes =
                    span.Slice(checked((int)(sha1TableOffset + index * 20L)), 20);

                var sha1 = Convert.ToHexStringLower(sha1Bytes);

                var crc32 =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        span.Slice(checked((int)(crcTableOffset + index * 4L)), 4));

                var offsetValue =
                    BinaryPrimitives.ReadUInt32BigEndian(
                        span.Slice(checked((int)(offsetTableOffset + index * 4L)), 4));

                long offset;

                if ((offsetValue & 0x80000000u) is not 0)
                {
                    var largeOffsetIndex = offsetValue & 0x7FFFFFFFu;

                    if (largeOffsetIndex >= largeOffsetCount)
                    {
                        throw Invalid(
                            $"Large-offset table index {largeOffsetIndex} is out of range " +
                            $"for {largeOffsetCount} entries.");
                    }

                    var largeOffsetPosition =
                        checked(largeOffsetTableOffset + largeOffsetIndex * 8L);

                    var largeOffsetValue =
                        BinaryPrimitives.ReadUInt64BigEndian(
                            span.Slice(checked((int)largeOffsetPosition), 8));

                    if (largeOffsetValue > long.MaxValue)
                        throw Invalid($"Pack offset {largeOffsetValue} exceeds Int64.");

                    offset = (long)largeOffsetValue;
                }
                else
                {
                    offset = offsetValue;
                }

                entries.Add(new IndexEntry(offset, sha1, crc32));
            }

            entries.Sort((left, right) => left.Offset.CompareTo(right.Offset));
            return entries;
        }
        catch (InvalidPackIndexException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Invalid("Pack index is malformed.", exception);
        }
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
        // We'll track both regular objects and deltas
        var regularObjects = new List<(long Offset, string SHA1, uint CRC32)>();

        var deltaObjects =
            new List<(long Offset, PackFile.ObjectType Type, long BaseOffset, string? BaseSHA1, byte[] DeltaData, uint CRC32)>();

        var offset = 12; // After header
        var sourceArray = packDataWithoutChecksum.Span.ToArray();

        // First pass: parse all objects and collect delta information
        for (var i = 0; i < objectCount; i++)
        {
            var startOffset = offset;

            // Parse object header
            var currentByte = sourceArray[offset++];
            var objectType = (PackFile.ObjectType)((currentByte >> 4) & 0x7);
            long size = currentByte & 0xF;
            var shift = 4;

            while ((currentByte & 0x80) is not 0)
            {
                currentByte = sourceArray[offset++];
                size |= (long)(currentByte & 0x7F) << shift;
                shift += 7;
            }

            // Handle delta objects specially
            if (objectType is PackFile.ObjectType.OfsDelta)
            {
                // OfsDelta: Read the negative offset encoding
                var negativeOffset = 0L;
                currentByte = sourceArray[offset++];
                negativeOffset = currentByte & 0x7F;

                while ((currentByte & 0x80) is not 0)
                {
                    currentByte = sourceArray[offset++];
                    negativeOffset = ((negativeOffset + 1) << 7) | ((long)currentByte & 0x7F);
                }

                var baseOffset = startOffset - negativeOffset;

                // Now follows the compressed delta data
                var deltaCompressedLength = PackFile.FindCompressedLength(sourceArray, offset, (int)size);
                var compressedData = sourceArray.AsSpan().Slice(offset, deltaCompressedLength);
                var deltaData = PackFile.DecompressZlib(compressedData, (int)size);

                var packedSize = (offset - startOffset) + deltaCompressedLength;
                var packedData = sourceArray.AsSpan().Slice(startOffset, packedSize);
                var crc32 = CalculateCRC32(packedData);

                deltaObjects.Add((startOffset, objectType, baseOffset, null, deltaData, crc32));
                offset += deltaCompressedLength;
            }
            else if (objectType is PackFile.ObjectType.RefDelta)
            {
                // RefDelta: Read the 20-byte SHA1 reference
                var baseSHA1Bytes = sourceArray.AsSpan().Slice(offset, 20);
                var baseSHA1 = Convert.ToHexStringLower(baseSHA1Bytes);
                offset += 20;

                // Now follows the compressed delta data
                var refDeltaCompressedLength = PackFile.FindCompressedLength(sourceArray, offset, (int)size);
                var compressedData = sourceArray.AsSpan().Slice(offset, refDeltaCompressedLength);
                var deltaData = PackFile.DecompressZlib(compressedData, (int)size);

                var packedSize = (offset - startOffset) + refDeltaCompressedLength;
                var packedData = sourceArray.AsSpan().Slice(startOffset, packedSize);
                var crc32 = CalculateCRC32(packedData);

                deltaObjects.Add((startOffset, objectType, -1, baseSHA1, deltaData, crc32));
                offset += refDeltaCompressedLength;
            }
            else
            {
                // Regular object (commit, tree, blob, tag)
                // Decompress to find the actual compressed length and calculate SHA1
                var compressedLength = PackFile.FindCompressedLength(sourceArray, offset, (int)size);
                var packedSize = (offset - startOffset) + compressedLength;

                // Calculate CRC32 of the complete packed object
                var packedData = sourceArray.AsSpan().Slice(startOffset, packedSize);
                var crc32 = CalculateCRC32(packedData);

                // Decompress to calculate SHA1
                var compressedData = sourceArray.AsSpan().Slice(offset, compressedLength);
                var decompressed = PackFile.DecompressZlib(compressedData, (int)size);

                // Calculate SHA1
                var objectHeader = System.Text.Encoding.UTF8.GetBytes($"{objectType.ToString().ToLower()} {size}\0");
                var dataForHash = new byte[objectHeader.Length + decompressed.Length];
                Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
                Array.Copy(decompressed, 0, dataForHash, objectHeader.Length, decompressed.Length);
                var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
                var sha1Hex = Convert.ToHexStringLower(sha1);

                regularObjects.Add((startOffset, sha1Hex, crc32));
                offset += compressedLength;
            }
        }

        // Second pass: reconstruct delta objects
        // Build lookup maps for quick access
        var objectsByOffset =
            regularObjects.ToDictionary(
                o => o.Offset,
                o => (o.SHA1, Data: (byte[]?)null, Type: (PackFile.ObjectType?)null));

        var objectsBySHA1 =
            regularObjects.ToDictionary(
                o => o.SHA1,
                o => (o.Offset, Data: (byte[]?)null, Type: (PackFile.ObjectType?)null));

        // Also track delta objects by offset for chain resolution
        var deltasByOffset = deltaObjects.ToDictionary(d => d.Offset);

        // Helper function to get object data by offset (handles delta chains)
        (byte[] Data, PackFile.ObjectType Type) GetObjectDataByOffset(long objOffset)
        {
            // Check if it's a regular object we've cached
            if (objectsByOffset.TryGetValue(objOffset, out var cached) && cached.Data is not null &&
                cached.Type is not null)
            {
                return (cached.Data, cached.Type.Value);
            }

            // Check if it's a delta object
            if (deltasByOffset.TryGetValue(objOffset, out var delta))
            {
                // Recursively resolve the base
                byte[] baseData;
                PackFile.ObjectType baseType;

                if (delta.Type == PackFile.ObjectType.OfsDelta)
                {
                    (baseData, baseType) = GetObjectDataByOffset(delta.BaseOffset);
                }
                else // RefDelta
                {
                    (baseData, baseType) = GetObjectDataBySHA1(delta.BaseSHA1!);
                }

                // Apply delta to reconstruct
                var reconstructedData = PackFile.ApplyDelta(baseData, delta.DeltaData);
                return (reconstructedData, baseType);
            }

            // Parse a regular object at this offset
            var pos = (int)objOffset;

            var currentByte = sourceArray[pos++];
            var objType = (PackFile.ObjectType)((currentByte >> 4) & 0x7);
            long objSize = currentByte & 0xF;
            var objShift = 4;

            while ((currentByte & 0x80) is not 0)
            {
                currentByte = sourceArray[pos++];
                objSize |= (long)(currentByte & 0x7F) << objShift;
                objShift += 7;
            }

            // Decompress the regular object
            var compLength = PackFile.FindCompressedLength(sourceArray, pos, (int)objSize);
            var compData = sourceArray.AsSpan().Slice(pos, compLength);
            var data = PackFile.DecompressZlib(compData, (int)objSize);

            // Cache it
            if (objectsByOffset.TryGetValue(objOffset, out var entry))
            {
                objectsByOffset[objOffset] = (entry.SHA1, data, objType);

                if (objectsBySHA1.ContainsKey(entry.SHA1))
                {
                    objectsBySHA1[entry.SHA1] = (objOffset, data, objType);
                }
            }

            return (data, objType);
        }

        // Helper function to get object data and type by SHA1
        (byte[] Data, PackFile.ObjectType Type) GetObjectDataBySHA1(string sha1)
        {
            if (objectsBySHA1.TryGetValue(sha1, out var cached) && cached.Data is not null && cached.Type is not null)
            {
                return (cached.Data, cached.Type.Value);
            }

            // Get by offset
            var objOffset = objectsBySHA1[sha1].Offset;
            return GetObjectDataByOffset(objOffset);
        }

        // Reconstruct delta objects
        var reconstructedObjects = new List<(long Offset, string SHA1, uint CRC32)>();

        foreach (var delta in deltaObjects)
        {
            var (reconstructedData, baseType) = GetObjectDataByOffset(delta.Offset);

            // Calculate SHA1 of the reconstructed object
            var objectHeader =
                System.Text.Encoding.UTF8.GetBytes($"{baseType.ToString().ToLower()} {reconstructedData.Length}\0");

            var dataForHash = new byte[objectHeader.Length + reconstructedData.Length];
            Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
            Array.Copy(reconstructedData, 0, dataForHash, objectHeader.Length, reconstructedData.Length);
            var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
            var sha1Hex = Convert.ToHexStringLower(sha1);

            reconstructedObjects.Add((delta.Offset, sha1Hex, delta.CRC32));
        }

        // Combine regular and reconstructed objects
        var allObjects = regularObjects.Concat(reconstructedObjects).ToList();

        // Sort objects by SHA1 for the index file
        var sortedObjects = allObjects.OrderBy(o => o.SHA1, StringComparer.Ordinal).ToList();

        // Build pack index v2
        var indexData = BuildPackIndexV2(sortedObjects, packFileData[^20..]);

        // Build reverse index (using original order which is by offset)
        var objectsInPackOrder = allObjects.OrderBy(o => o.Offset).ToList();
        var reverseIndexData = BuildReverseIndexV1(objectsInPackOrder, sortedObjects, packFileData[^20..]);

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
            BinaryPrimitives.WriteUInt32BigEndian(
                span.Slice(offsetTableOffset + i * 4, 4),
                (uint)sortedObjects[i].Offset);
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
