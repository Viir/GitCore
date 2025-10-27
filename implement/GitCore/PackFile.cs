using System;
using System.Buffers.Binary;

namespace GitCore;

public static class PackFile
{
    public record PackFileHeader(
        uint Version,
        uint ObjectCount);

    public static PackFileHeader ParsePackFileHeader(ReadOnlyMemory<byte> packFileData)
    {
        if (packFileData.Length < 12)
        {
            throw new ArgumentException("Pack file is too small to contain a valid header");
        }

        var span = packFileData.Span;

        // Verify PACK signature
        if (span[0] != 'P' || span[1] != 'A' || span[2] != 'C' || span[3] != 'K')
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
        return packFileData.Slice(packFileData.Length - 20);
    }

    public static bool VerifyPackFileChecksum(ReadOnlyMemory<byte> packFileData)
    {
        if (packFileData.Length < 20)
        {
            return false;
        }

        // Get the stored checksum (last 20 bytes)
        var storedChecksum = packFileData.Slice(packFileData.Length - 20);

        // Calculate checksum of everything except the last 20 bytes
        var dataToHash = packFileData.Slice(0, packFileData.Length - 20);
        var calculatedChecksum = System.Security.Cryptography.SHA1.HashData(dataToHash.Span);

        // Compare checksums
        return storedChecksum.Span.SequenceEqual(calculatedChecksum);
    }
}
