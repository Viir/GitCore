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
        string SHA1);

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

    public static IReadOnlyList<PackObject> ParseAllObjects(ReadOnlyMemory<byte> packFileData, IReadOnlyList<PackIndex.IndexEntry>? indexEntries = null)
    {
        var header = ParsePackFileHeader(packFileData);
        var objects = new List<PackObject>();

        var dataWithoutChecksum = packFileData[..^20];

        if (indexEntries is not null && indexEntries.Count > 0)
        {
            // Use index file for accurate offsets
            for (var i = 0; i < indexEntries.Count; i++)
            {
                var entry = indexEntries[i];
                var offset = (int)entry.Offset;

                // Determine compressed size from next offset or end of data
                int compressedSize;
                if (i + 1 < indexEntries.Count)
                {
                    compressedSize = (int)(indexEntries[i + 1].Offset - entry.Offset);
                }
                else
                {
                    compressedSize = dataWithoutChecksum.Length - offset;
                }

                var packObject = ParseObjectAtWithSize(dataWithoutChecksum, offset, compressedSize);

                // Verify SHA1 matches
                if (packObject.SHA1 != entry.SHA1)
                {
                    throw new InvalidOperationException($"SHA1 mismatch: expected {entry.SHA1}, got {packObject.SHA1}");
                }

                objects.Add(packObject);
            }
        }
        else
        {
            // Fallback: parse without index (less reliable due to ZLibStream buffering)
            var offset = 12; // Skip header

            for (var i = 0; i < header.ObjectCount; i++)
            {
                var (packObject, bytesRead) = ParseObjectAt(dataWithoutChecksum, offset);
                objects.Add(packObject);
                offset += bytesRead;
            }
        }

        return objects;
    }

    private static PackObject ParseObjectAtWithSize(ReadOnlyMemory<byte> packFileData, int offset, int totalSize)
    {
        var span = packFileData.Span;
        var startOffset = offset;

        // Read object type and size from variable-length encoding
        var currentByte = span[offset++];
        var objectType = (ObjectType)((currentByte >> 4) & 0x7);
        long size = currentByte & 0xF;
        var shift = 4;

        // Continue reading size if MSB is set
        while ((currentByte & 0x80) != 0)
        {
            currentByte = span[offset++];
            size |= (long)(currentByte & 0x7F) << shift;
            shift += 7;
        }

        // Calculate actual compressed data size (total size minus header bytes we just read)
        var headerBytesRead = offset - startOffset;
        var compressedDataSize = totalSize - headerBytesRead;

        // Extract the compressed data
        var compressedData = packFileData.Slice(offset, compressedDataSize);
        var decompressedData = DecompressZlibFixed(compressedData.Span, (int)size);

        // Calculate SHA1 of the decompressed object
        var objectHeader = System.Text.Encoding.UTF8.GetBytes($"{objectType.ToString().ToLower()} {size}\0");
        var dataForHash = new byte[objectHeader.Length + decompressedData.Length];
        Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
        Array.Copy(decompressedData, 0, dataForHash, objectHeader.Length, decompressedData.Length);
        var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
        var sha1Hex = Convert.ToHexStringLower(sha1);

        return new PackObject(objectType, size, decompressedData, sha1Hex);
    }

    private static (PackObject, int) ParseObjectAt(ReadOnlyMemory<byte> packFileData, int offset)
    {
        var span = packFileData.Span;
        var startOffset = offset;

        if (offset >= span.Length)
        {
            throw new ArgumentException($"Offset {offset} is beyond data length {span.Length}");
        }

        // Read object type and size from variable-length encoding
        var currentByte = span[offset++];
        var objectType = (ObjectType)((currentByte >> 4) & 0x7);
        long size = currentByte & 0xF;
        var shift = 4;

        // Continue reading size if MSB is set
        while ((currentByte & 0x80) != 0)
        {
            if (offset >= span.Length)
            {
                throw new ArgumentException($"Unexpected end of data while reading object size at offset {offset}");
            }
            currentByte = span[offset++];
            size |= (long)(currentByte & 0x7F) << shift;
            shift += 7;
        }

        // Read compressed data using zlib
        var compressedData = packFileData[offset..];
        var decompressedData = DecompressZlib(compressedData.Span, (int)size, out var compressedBytesRead);

        offset += compressedBytesRead;

        // Calculate SHA1 of the decompressed object
        var objectHeader = System.Text.Encoding.UTF8.GetBytes($"{objectType.ToString().ToLower()} {size}\0");
        var dataForHash = new byte[objectHeader.Length + decompressedData.Length];
        Array.Copy(objectHeader, 0, dataForHash, 0, objectHeader.Length);
        Array.Copy(decompressedData, 0, dataForHash, objectHeader.Length, decompressedData.Length);
        var sha1 = System.Security.Cryptography.SHA1.HashData(dataForHash);
        var sha1Hex = Convert.ToHexStringLower(sha1);

        var packObject = new PackObject(objectType, size, decompressedData, sha1Hex);
        var totalBytesRead = offset - startOffset;

        return (packObject, totalBytesRead);
    }

    private static byte[] DecompressZlibFixed(ReadOnlySpan<byte> compressedData, int expectedSize)
    {
        using var inputStream = new System.IO.MemoryStream(compressedData.ToArray());
        using var zlibStream = new ZLibStream(inputStream, CompressionMode.Decompress);
        
        var result = new byte[expectedSize];
        zlibStream.ReadExactly(result);
        
        return result;
    }

    private static byte[] DecompressZlib(ReadOnlySpan<byte> compressedData, int expectedSize, out int bytesRead)
    {
        // Wrap the data in a non-seekable stream that tracks actual reads
        var countingWrapper = new NonSeekableCountingStream(compressedData.ToArray());

        using var zlibStream = new ZLibStream(countingWrapper, CompressionMode.Decompress, leaveOpen: true);

        var result = new byte[expectedSize];
        zlibStream.ReadExactly(result);

        bytesRead = countingWrapper.BytesRead;
        return result;
    }

    private class NonSeekableCountingStream(byte[] data) : System.IO.Stream
    {
        private readonly byte[] _data = data;
        private int _position = 0;
        public int BytesRead => _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesToRead = Math.Min(count, _data.Length - _position);
            if (bytesToRead > 0)
            {
                Array.Copy(_data, _position, buffer, offset, bytesToRead);
                _position += bytesToRead;
            }
            return bytesToRead;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static IReadOnlyDictionary<string, PackObject> GetObjectsBySHA1(IReadOnlyList<PackObject> objects)
    {
        return objects.ToDictionary(obj => obj.SHA1, obj => obj);
    }
}
