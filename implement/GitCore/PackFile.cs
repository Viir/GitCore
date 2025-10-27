using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;

namespace GitCore;

public static class PackFile
{
    public enum ObjectType
    {
        Commit = 1,
        Tree = 2,
        Blob = 3,
        Tag = 4,
        OfsDelta = 6,
        RefDelta = 7
    }

    public record PackFileHeader(
        uint Version,
        uint ObjectCount);

    public record PackObject(
        ObjectType Type,
        long Size,
        ReadOnlyMemory<byte> Data,
        string SHA1base16);

    public static PackFileHeader ParsePackFileHeader(ReadOnlyMemory<byte> packFileData)
    {
        if (packFileData.Length < 12)
        {
            throw new ArgumentException("Pack file is too small to contain a valid header");
        }

        var span = packFileData.Span;

        // Verify PACK signature
        if (!span[..4].SequenceEqual("PACK"u8))
        {
            throw new ArgumentException("Invalid pack file signature");
        }

        // Read version (big-endian)
        var version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));

        // Read object count (big-endian)
        var objectCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));

        return new PackFileHeader(version, objectCount);
    }

    public static ReadOnlyMemory<byte> GetPackFileChecksum(ReadOnlyMemory<byte> packFileData)
    {
        if (packFileData.Length < 20)
        {
            throw new ArgumentException("Pack file is too small to contain a checksum");
        }

        // Last 20 bytes are the SHA-1 checksum
        return packFileData[^20..];
    }

    public static bool VerifyPackFileChecksum(ReadOnlyMemory<byte> packFileData)
    {
        if (packFileData.Length < 20)
        {
            return false;
        }

        // Get the stored checksum (last 20 bytes)
        var storedChecksum = packFileData[^20..];

        // Calculate checksum of everything except the last 20 bytes
        var dataToHash = packFileData[..^20];
        var calculatedChecksum = System.Security.Cryptography.SHA1.HashData(dataToHash.Span);

        // Compare checksums
        return storedChecksum.Span.SequenceEqual(calculatedChecksum);
    }

    public static IReadOnlyList<PackObject> ParseAllObjects(
        ReadOnlyMemory<byte> packFileData,
        IReadOnlyList<PackIndex.IndexEntry> indexEntries)
    {
        var header = ParsePackFileHeader(packFileData);
        var objects = new List<PackObject>();

        var dataWithoutChecksum = packFileData[..^20];

        // Build a map of offset to index entry for quick lookup
        var entriesByOffset = indexEntries.ToDictionary(e => e.Offset, e => e);

        // First pass: parse regular objects and store them for delta reconstruction
        var objectsByOffset = new Dictionary<long, (ObjectType Type, byte[] Data)>();
        var objectsBySHA1 = new Dictionary<string, (ObjectType Type, byte[] Data)>();

        // Helper to parse an object at a given offset
        (ObjectType Type, byte[] Data) ParseObjectAt(int objOffset)
        {
            if (objectsByOffset.TryGetValue(objOffset, out var cached))
            {
                return cached;
            }

            var span = dataWithoutChecksum.Span;
            var pos = objOffset;
            var startPos = pos;

            // Read object type and size from variable-length encoding
            var currentByte = span[pos++];
            var objectType = (ObjectType)((currentByte >> 4) & 0x7);
            long size = currentByte & 0xF;
            var shift = 4;

            // Continue reading size if MSB is set
            while ((currentByte & 0x80) != 0)
            {
                currentByte = span[pos++];
                size |= (long)(currentByte & 0x7F) << shift;
                shift += 7;
            }

            // Handle delta objects
            if (objectType == ObjectType.OfsDelta)
            {
                // Read negative offset
                var negativeOffset = 0L;
                currentByte = span[pos++];
                negativeOffset = currentByte & 0x7F;
                
                while ((currentByte & 0x80) != 0)
                {
                    currentByte = span[pos++];
                    negativeOffset = ((negativeOffset + 1) << 7) | ((long)currentByte & 0x7F);
                }

                var baseOffset = startPos - negativeOffset;

                // Get base object
                var (baseType, baseData) = ParseObjectAt((int)baseOffset);

                // Decompress delta data
                // Find compressed length by trying to decompress
                var compressedLength = FindCompressedLengthForDelta(span, pos, (int)size);
                var compressedData = span.Slice(pos, compressedLength);
                var deltaData = DecompressZlib(compressedData, (int)size);

                // Apply delta
                var reconstructedData = ApplyDelta(baseData, deltaData);

                // Cache and return
                var result = (baseType, reconstructedData);
                objectsByOffset[objOffset] = result;
                return result;
            }
            else if (objectType == ObjectType.RefDelta)
            {
                // Read base SHA1
                var baseSHA1Bytes = span.Slice(pos, 20);
                var baseSHA1 = Convert.ToHexStringLower(baseSHA1Bytes);
                pos += 20;

                // Get base object
                if (!objectsBySHA1.TryGetValue(baseSHA1, out var baseObj))
                {
                    // Need to find the base object by SHA1
                    // Search through entries
                    var baseEntry = indexEntries.FirstOrDefault(e => e.SHA1base16 == baseSHA1);
                    if (baseEntry == null)
                    {
                        throw new InvalidOperationException($"Base object {baseSHA1} not found for RefDelta");
                    }
                    baseObj = ParseObjectAt((int)baseEntry.Offset);
                }

                // Decompress delta data
                var compressedLength = FindCompressedLengthForDelta(span, pos, (int)size);
                var compressedData = span.Slice(pos, compressedLength);
                var deltaData = DecompressZlib(compressedData, (int)size);

                // Apply delta
                var reconstructedData = ApplyDelta(baseObj.Data, deltaData);

                // Cache and return
                var result = (baseObj.Type, reconstructedData);
                objectsByOffset[objOffset] = result;
                return result;
            }
            else
            {
                // Regular object
                // Find compressed length
                var compressedLength = FindCompressedLengthForRegular(span, pos, (int)size);
                var compressedData = span.Slice(pos, compressedLength);
                var decompressedData = DecompressZlib(compressedData, (int)size);

                var result = (objectType, decompressedData);
                objectsByOffset[objOffset] = result;
                return result;
            }
        }

        // Helper to find compressed length for regular objects
        int FindCompressedLengthForRegular(ReadOnlySpan<byte> data, int offset, int expectedSize)
        {
            var inflater = new ICSharpCode.SharpZipLib.Zip.Compression.Inflater(false);
            try
            {
                var availableData = data[offset..].ToArray();
                inflater.SetInput(availableData);
                var outputBuffer = new byte[expectedSize + 1];
                var decompressedBytes = inflater.Inflate(outputBuffer);
                if (decompressedBytes != expectedSize)
                {
                    throw new InvalidOperationException($"Decompression size mismatch at offset {offset}");
                }
                return (int)inflater.TotalIn;
            }
            finally
            {
                inflater.Reset();
            }
        }

        // Helper to find compressed length for delta objects
        int FindCompressedLengthForDelta(ReadOnlySpan<byte> data, int offset, int expectedSize)
        {
            return FindCompressedLengthForRegular(data, offset, expectedSize);
        }

        // Parse all objects using the index
        for (var i = 0; i < indexEntries.Count; i++)
        {
            var entry = indexEntries[i];
            var offset = (int)entry.Offset;

            var (objectType, decompressedData) = ParseObjectAt(offset);

            // Store in SHA1 map for RefDelta lookups
            objectsBySHA1[entry.SHA1base16] = (objectType, decompressedData);

            // Create PackObject
            var packObject = new PackObject(objectType, decompressedData.Length, decompressedData, entry.SHA1base16);

            // Verify SHA1 matches
            var objectHeader = System.Text.Encoding.UTF8.GetBytes($"{objectType.ToString().ToLower()} {decompressedData.Length}\0");
            var dataForHash = new byte[objectHeader.Length + decompressedData.Length];
            Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
            Array.Copy(decompressedData, 0, dataForHash, objectHeader.Length, decompressedData.Length);
            var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
            var sha1Hex = Convert.ToHexStringLower(sha1);

            if (sha1Hex != entry.SHA1base16)
            {
                throw new InvalidOperationException($"SHA1 mismatch: expected {entry.SHA1base16}, got {sha1Hex}");
            }

            objects.Add(packObject);
        }

        return objects;
    }

    public static byte[] DecompressZlib(ReadOnlySpan<byte> compressedData, int expectedSize)
    {
        using var inputStream = new System.IO.MemoryStream(compressedData.ToArray());
        using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);

        var result = new byte[expectedSize];
        zlibStream.ReadExactly(result);

        return result;
    }

    public static IReadOnlyDictionary<string, PackObject> GetObjectsBySHA1(IReadOnlyList<PackObject> objects)
    {
        return objects.ToDictionary(obj => obj.SHA1base16, obj => obj);
    }

    /// <summary>
    /// Applies delta instructions to reconstruct an object from a base object.
    /// </summary>
    /// <param name="baseData">The base object data</param>
    /// <param name="deltaData">The delta instructions</param>
    /// <returns>The reconstructed object data</returns>
    public static byte[] ApplyDelta(ReadOnlySpan<byte> baseData, ReadOnlySpan<byte> deltaData)
    {
        var offset = 0;

        // Read base object size (variable-length encoding)
        var baseSize = ReadDeltaSize(deltaData, ref offset);

        if (baseSize != baseData.Length)
        {
            throw new InvalidOperationException($"Base size mismatch: expected {baseSize}, got {baseData.Length}");
        }

        // Read result object size (variable-length encoding)
        var resultSize = ReadDeltaSize(deltaData, ref offset);

        // Build the result
        var result = new byte[resultSize];
        var resultOffset = 0;

        while (offset < deltaData.Length)
        {
            var cmd = deltaData[offset++];

            if ((cmd & 0x80) != 0)
            {
                // Copy command
                var copyOffset = 0;
                var copySize = 0;

                // Read offset (up to 4 bytes)
                if ((cmd & 0x01) != 0) copyOffset = deltaData[offset++];
                if ((cmd & 0x02) != 0) copyOffset |= deltaData[offset++] << 8;
                if ((cmd & 0x04) != 0) copyOffset |= deltaData[offset++] << 16;
                if ((cmd & 0x08) != 0) copyOffset |= deltaData[offset++] << 24;

                // Read size (up to 3 bytes)
                if ((cmd & 0x10) != 0) copySize = deltaData[offset++];
                if ((cmd & 0x20) != 0) copySize |= deltaData[offset++] << 8;
                if ((cmd & 0x40) != 0) copySize |= deltaData[offset++] << 16;

                // Size 0 means 0x10000
                if (copySize == 0) copySize = 0x10000;

                // Copy from base
                baseData.Slice(copyOffset, copySize).CopyTo(result.AsSpan(resultOffset, copySize));
                resultOffset += copySize;
            }
            else if (cmd != 0)
            {
                // Insert command - cmd bytes follow
                var insertSize = cmd;
                deltaData.Slice(offset, insertSize).CopyTo(result.AsSpan(resultOffset, insertSize));
                offset += insertSize;
                resultOffset += insertSize;
            }
            else
            {
                throw new InvalidOperationException("Invalid delta instruction: zero byte");
            }
        }

        if (resultOffset != resultSize)
        {
            throw new InvalidOperationException($"Delta reconstruction size mismatch: expected {resultSize}, got {resultOffset}");
        }

        return result;
    }

    private static int ReadDeltaSize(ReadOnlySpan<byte> data, ref int offset)
    {
        var size = 0;
        var shift = 0;
        byte b;

        do
        {
            b = data[offset++];
            size |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        return size;
    }
}
