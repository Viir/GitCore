using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace GitCore;

public static class PackIndex
{
    public record IndexEntry(
        long Offset,
        string SHA1);

    public static IReadOnlyList<IndexEntry> ParsePackIndexV2(ReadOnlyMemory<byte> indexData)
    {
        var span = indexData.Span;
        
        // Check magic number
        if (span[0] != 0xFF || span[1] != 't' || span[2] != 'O' || span[3] != 'c')
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
        
        for (int i = 0; i < objectCount; i++)
        {
            // Read SHA-1 (20 bytes)
            var sha1Bytes = span.Slice(sha1TableOffset + i * 20, 20);
            var sha1 = Convert.ToHexStringLower(sha1Bytes);
            
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
            
            entries.Add(new IndexEntry(offset, sha1));
        }
        
        // Sort by offset to make it easier to determine object sizes
        entries.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        
        return entries;
    }
}
