using AwesomeAssertions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace GitCore.UnitTests;

public class GitIndexTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Index_reads_versions_modes_stages_and_unicode(int version)
    {
        var index = GitIndex.Parse(BuildIndex(version,
        [
            new("README.md", AssumeValid: true),
            new("conflict", Stage: 1),
            new("conflict", Stage: 2),
            new("conflict", Stage: 3),
            new("executable", Mode: 0x81ed),
            new("link", Mode: 0xa000),
            new("submodule", Mode: 0xe000),
            new("目录/🙂.txt"),
        ]));

        index.Entries.Count.Should().Be(8);
        index.Entries[0].Path.Should().Be("README.md");
        index.Entries[0].AssumeValid.Should().BeTrue();
        index.Entries[0].ObjectId.Should().Be(new string('1', 40));
        index.Entries.Where(entry => entry.Path == "conflict").Select(entry => entry.Stage).Should().Equal(1, 2, 3);
        index.Entries[4].Mode.Should().Be(0x81edu);
        index.IsTracked("conflict").Should().BeTrue();
        index.IsTracked("目录/🙂.txt").Should().BeTrue();
        index.IsTracked("README.md/descendant").Should().BeFalse();
        index.IsTracked("目录").Should().BeFalse();
        index.IsTracked("readme.md").Should().BeFalse();
        index.IsTracked("readme.md", ignoreCase: true).Should().BeTrue();
        index.IsTracked("./README.md").Should().BeFalse();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Index_ignore_case_folds_ascii_but_preserves_distinct_unicode_paths(int version)
    {
        var index = GitIndex.Parse(BuildIndex(version, [new("ASCII.TXT"), new("É.TXT")]));

        index.IsTracked("ascii.txt").Should().BeFalse();
        index.IsTracked("ascii.txt", ignoreCase: true).Should().BeTrue();
        index.IsTracked("É.txt").Should().BeFalse();
        index.IsTracked("É.txt", ignoreCase: true).Should().BeTrue();
        index.IsTracked("é.TXT", ignoreCase: true).Should().BeFalse();
        index.IsTracked("é.txt", ignoreCase: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void Index_tracks_skip_worktree_and_intent_to_add(int version)
    {
        var index = GitIndex.Parse(BuildIndex(version,
        [
            new("intent", ExtendedFlags: 0x2000, ObjectByte: 0),
            new("skip", ExtendedFlags: 0x4000),
            new("skip-intent", AssumeValid: true, ExtendedFlags: 0x6000),
        ]));

        index.IsTracked("intent").Should().BeTrue();
        index.IsTracked("skip").Should().BeTrue();
        index.Entries[1].SkipWorktree.Should().BeTrue();
        index.Entries[0].SkipWorktree.Should().BeFalse();
        index.Entries[2].AssumeValid.Should().BeTrue();
        index.Entries[0].ObjectId.Should().Be(new string('0', 40));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Index_reads_sha256_and_validates_its_checksum(int version)
    {
        var bytes = BuildIndex(version, [new("file")], objectIdLength: 32);
        var index = GitIndex.Parse(bytes, objectIdLength: 32);

        index.Entries[0].ObjectId.Should().Be(new string('1', 64));
        bytes[^1] ^= 1;
        Throws<InvalidDataException>(() => GitIndex.Parse(bytes, objectIdLength: 32));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Index_handles_empty_indexes_and_readonly_entries(int version)
    {
        GitIndex.Empty.Entries.Should().BeEmpty();
        GitIndex.Empty.IsTracked("anything").Should().BeFalse();
        var emptyList = GitIndex.Empty.Entries.Should().BeAssignableTo<IList<GitIndexEntry>>().Subject;
        Throws<NotSupportedException>(() => emptyList.Clear());

        var empty = GitIndex.Parse(BuildIndex(version, []));
        empty.Entries.Should().BeEmpty();
        empty.IsTracked("anything").Should().BeFalse();

        var index = GitIndex.Parse(BuildIndex(version, [new("file")]));
        var list = index.Entries.Should().BeAssignableTo<IList<GitIndexEntry>>().Subject;
        Throws<NotSupportedException>(() => list.Clear());
        index.IsTracked("file").Should().BeTrue();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Index_reads_all_padding_alignments_and_long_paths(int version)
    {
        var entries = Enumerable.Range(1, 16).Select(length => new FixtureEntry(new string('a', length))).ToArray();
        var index = GitIndex.Parse(BuildIndex(version, entries));
        index.Entries.Select(entry => entry.Path).Should().Equal(entries.Select(entry => entry.Path));

        var longPath = new string('a', 4096);
        GitIndex.Parse(BuildIndex(version, [new(longPath)])).IsTracked(longPath).Should().BeTrue();
    }

    [Fact]
    public void Index_v4_decodes_multibyte_removal_and_unicode_byte_prefixes_without_padding()
    {
        var longPath = "a" + new string('x', 300);
        var index = GitIndex.Parse(BuildIndex(4,
        [
            new(longPath),
            new("b"),
            new("目录/文"),
            new("目录/斌"),
        ]));

        index.Entries.Select(entry => entry.Path).Should().Equal(longPath, "b", "目录/文", "目录/斌");
    }

    [Fact]
    public void Index_uses_utf8_byte_order_rather_than_utf16_order()
    {
        var index = GitIndex.Parse(BuildIndex(4, [new("\ue000"), new("𐀀")]));
        index.IsTracked("\ue000").Should().BeTrue();
        index.IsTracked("𐀀").Should().BeTrue();
    }

    [Fact]
    public void Index_ignores_optional_extensions_but_rejects_unknown_mandatory_extensions()
    {
        var index = GitIndex.Parse(BuildIndex(2, [new("file")], [("TEST", new byte[] { 1, 2, 3 }), ("TREE", Array.Empty<byte>())]));
        index.IsTracked("file").Should().BeTrue();

        var exception = Throws<NotSupportedException>(() => GitIndex.Parse(BuildIndex(2, [], [("abcd", Array.Empty<byte>())])));
        exception.Message.Should().Contain("abcd");
    }

    [Fact]
    public void Index_sparse_directories_are_exact_entries_and_descendants_are_not_silently_untracked()
    {
        var index = GitIndex.Parse(BuildIndex(3,
        [
            new("included"),
            new("sparse/", Mode: 0x4000, ExtendedFlags: 0x4000),
        ], [("sdir", Array.Empty<byte>())]));

        index.Entries[1].IsSparseDirectory.Should().BeTrue();
        index.IsTracked("sparse/").Should().BeTrue();
        index.IsTracked("sparse").Should().BeFalse();
        index.IsTracked("sparse-other/file").Should().BeFalse();
        index.IsTracked("SPARSE/file").Should().BeFalse();
        Throws<NotSupportedException>(() => index.IsTracked("sparse/file"));
        Throws<NotSupportedException>(() => index.IsTracked("SPARSE/file", ignoreCase: true));
    }

    [Fact]
    public void Index_sparse_directory_prefixes_fold_ascii_but_preserve_distinct_unicode_paths()
    {
        var index = GitIndex.Parse(BuildIndex(3,
        [
            new("ASCII/", Mode: 0x4000, ExtendedFlags: 0x4000),
            new("É/SUB/", Mode: 0x4000, ExtendedFlags: 0x4000),
        ], [("sdir", Array.Empty<byte>())]));

        index.IsTracked("ascii/", ignoreCase: true).Should().BeTrue();
        index.IsTracked("É/sub/", ignoreCase: true).Should().BeTrue();
        index.IsTracked("é/SUB/", ignoreCase: true).Should().BeFalse();
        index.IsTracked("é/sub/file", ignoreCase: true).Should().BeFalse();
        index.IsTracked("É/sub/file").Should().BeFalse();
        Throws<NotSupportedException>(() => index.IsTracked("ascii/file", ignoreCase: true));
        Throws<NotSupportedException>(() => index.IsTracked("É/sub/file", ignoreCase: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/file")]
    [InlineData("file/")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    [InlineData("./file")]
    public void Index_rejects_invalid_paths(string path)
    {
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new(path)])));
    }

    [Fact]
    public void Index_rejects_invalid_utf8_and_accepts_literal_backslash_filenames()
    {
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new("ignored", RawPath: [0xc0, 0xaf])])));
        GitIndex.Parse(BuildIndex(2, [new(@"a\b")])).IsTracked(@"a\b").Should().BeTrue();
    }

    [Fact]
    public void Index_rejects_bad_signatures_versions_counts_checksums_and_truncation()
    {
        var valid = BuildIndex(2, [new("file")]);

        for (var length = 0; length < valid.Length; length++)
        {
            var truncated = valid[..length];
            Throws<InvalidDataException>(() => GitIndex.Parse(truncated));
        }

        var badSignature = (byte[])valid.Clone();
        badSignature[0] = (byte)'X';
        Rehash(badSignature);
        Throws<InvalidDataException>(() => GitIndex.Parse(badSignature));

        var badVersion = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(badVersion.AsSpan(4, 4), 5);
        Rehash(badVersion);
        Throws<NotSupportedException>(() => GitIndex.Parse(badVersion));

        var badCount = (byte[])valid.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(badCount.AsSpan(8, 4), uint.MaxValue);
        Rehash(badCount);
        Throws<InvalidDataException>(() => GitIndex.Parse(badCount));

        valid[^1] ^= 1;
        Throws<InvalidDataException>(() => GitIndex.Parse(valid));
        Throws<ArgumentOutOfRangeException>(() => GitIndex.Parse(valid, objectIdLength: 16));
    }

    [Fact]
    public void Index_rejects_truncated_entry_data_even_with_a_correct_checksum()
    {
        var valid = BuildIndex(2, [new("file")]);

        for (var length = 12; length < valid.Length - 20; length++)
        {
            var truncated = AppendChecksum(valid.AsSpan(0, length).ToArray(), 20);
            Throws<InvalidDataException>(() => GitIndex.Parse(truncated));
        }
    }

    [Fact]
    public void Index_rejects_bad_padding_path_lengths_reserved_flags_modes_and_order()
    {
        var badPadding = BuildIndex(2, [new("file")]);
        badPadding[12 + 62 + 5] = 1;
        Rehash(badPadding);
        Throws<InvalidDataException>(() => GitIndex.Parse(badPadding));

        var badLength = BuildIndex(2, [new("file")]);
        badLength[12 + 61] = 3;
        Rehash(badLength);
        Throws<InvalidDataException>(() => GitIndex.Parse(badLength));

        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new("file", ExtendedFlags: 0x4000)])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(3, [new("file", ExtendedFlags: 1)])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(3, [new("file", ExtendedFlags: 0x8000)])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new("file", Mode: 0)])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new("z"), new("a")])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new("a"), new("a")])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [new("a", Stage: 3), new("a", Stage: 1)])));
    }

    [Fact]
    public void Index_rejects_malformed_v4_compression()
    {
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(4, [new("file", V4Prefix: [1])])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(4, [new("file", V4Prefix: [0xff, 0xff, 0xff, 0xff, 0xff, 0x7f])])));
        var unterminated = AppendChecksum(
            [.. "DIRC"u8, 0, 0, 0, 4, 0, 0, 0, 1, .. new byte[62], 0x80],
            20);
        Throws<InvalidDataException>(() => GitIndex.Parse(unterminated));
    }

    [Fact]
    public void Index_rejects_truncated_extensions_and_invalid_sparse_metadata()
    {
        var oversized = BuildIndex(2, [], [("TEST", Array.Empty<byte>())]);
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), uint.MaxValue);
        Rehash(oversized);
        Throws<InvalidDataException>(() => GitIndex.Parse(oversized));
        Throws<InvalidDataException>(() => GitIndex.Parse(AppendChecksum([.. "DIRC"u8, 0, 0, 0, 2, 0, 0, 0, 0, 1], 20)));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [], [("1BAD", Array.Empty<byte>())])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(3, [new("sparse/", Mode: 0x4000, ExtendedFlags: 0x4000)])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(3, [new("sparse/", Mode: 0x4000)], [("sdir", Array.Empty<byte>())])));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(3, [], [("sdir", new byte[] { 1 })])));
    }

    [Theory]
    [InlineData(2, 20)]
    [InlineData(3, 20)]
    [InlineData(4, 20)]
    [InlineData(4, 32)]
    public void Index_merges_split_index_delete_replace_and_new_entries(int version, int objectIdLength)
    {
        var shared = BuildIndex(version, [new("a"), new("b"), new("c"), new("conflict", Stage: 1)], objectIdLength: objectIdLength);
        var expectedId = Convert.ToHexStringLower(shared.AsSpan(shared.Length - objectIdLength));
        var link = BuildLink(shared.AsSpan(shared.Length - objectIdLength).ToArray(), BuildBitmap(4, [1]), BuildBitmap(4, [2, 3]));
        var split = BuildIndex(version,
        [
            new("", AssumeValid: true, ObjectByte: 0x22),
            new("", Stage: 2),
            new("added"),
        ], [("link", link)], objectIdLength);
        var resolverCalls = 0;

        var index = GitIndex.Parse(split, objectIdLength,
            checksum =>
            {
                resolverCalls++;
                checksum.Should().Be(expectedId);
                return shared;
            });

        resolverCalls.Should().Be(1);
        index.Entries.Select(entry => entry.Path).Should().Equal("a", "added", "c", "conflict");
        index.IsTracked("b").Should().BeFalse();
        index.IsTracked("conflict").Should().BeTrue();
        index.Entries[2].AssumeValid.Should().BeTrue();
        index.Entries[2].ObjectId.Should().Be(new string('2', objectIdLength * 2));
        index.Entries[3].Stage.Should().Be(2);
    }

    [Fact]
    public void Index_reads_split_index_without_changes_and_without_a_shared_base()
    {
        var shared = BuildIndex(2, [new("a")]);
        var linked = BuildIndex(2, [], [("link", shared[^20..])]);
        GitIndex.Parse(linked, readSharedIndex: _ => shared).IsTracked("a").Should().BeTrue();
        GitIndex.Parse(BuildIndex(2, [new("a")], [("link", new byte[20])])).IsTracked("a").Should().BeTrue();
        GitIndex.Parse(BuildIndex(2, [new("a")],
            [("link", BuildLink(new byte[20], BuildBitmap(0, []), BuildBitmap(0, [])))])).IsTracked("a").Should().BeTrue();
    }

    [Fact]
    public void Index_split_requires_resolver_and_validates_shared_identity_and_structure()
    {
        var shared = BuildIndex(2, [new("a")]);
        var link = BuildLink(shared[^20..], BuildBitmap(1, []), BuildBitmap(1, []));
        var split = BuildIndex(2, [], [("link", link)]);
        Throws<NotSupportedException>(() => GitIndex.Parse(split));
        Throws<InvalidDataException>(() => GitIndex.Parse(split, readSharedIndex: _ => BuildIndex(2, [new("b")])));
        Throws<InvalidDataException>(() => GitIndex.Parse(split, readSharedIndex: _ => ReadOnlyMemory<byte>.Empty));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [], [("link", link), ("link", link)]), readSharedIndex: _ => shared));
        Throws<InvalidDataException>(() => GitIndex.Parse(BuildIndex(2, [], [("link", new byte[19])])));

        var nested = BuildIndex(2, [], [("link", new byte[20])]);
        var nestedSplit = BuildIndex(2, [], [("link", nested[^20..])]);
        Throws<InvalidDataException>(() => GitIndex.Parse(nestedSplit, readSharedIndex: _ => nested));
    }

    [Fact]
    public void Index_split_rejects_inconsistent_replacements_and_out_of_range_bitmaps()
    {
        var shared = BuildIndex(2, [new("a"), new("b")]);

        void Reject(FixtureEntry[] entries, byte[] deleted, byte[] replaced)
        {
            var split = BuildIndex(2, entries, [("link", BuildLink(shared[^20..], deleted, replaced))]);
            Throws<InvalidDataException>(() => GitIndex.Parse(split, readSharedIndex: _ => shared));
        }

        Reject([new("")], BuildBitmap(2, [0]), BuildBitmap(2, [0]));
        Reject([], BuildBitmap(2, []), BuildBitmap(2, [0]));
        Reject([new("named")], BuildBitmap(2, []), BuildBitmap(2, [0]));
        Reject([new("")], BuildBitmap(2, []), BuildBitmap(2, []));
        Reject([new("a")], BuildBitmap(2, []), BuildBitmap(2, []));
        Reject([], BuildBitmap(3, [2]), BuildBitmap(2, []));

        var malformed = BuildBitmap(2, [0]);
        BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(4, 4), uint.MaxValue);
        Reject([], malformed, BuildBitmap(2, []));
        malformed = BuildBitmap(2, [0]);
        BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(malformed.Length - 4), 1);
        Reject([], malformed, BuildBitmap(2, []));
        malformed = BuildBitmap(2, [0]);
        BinaryPrimitives.WriteUInt64BigEndian(malformed.AsSpan(16, 8), 4);
        Reject([], malformed, BuildBitmap(2, []));
        malformed = BuildBitmap(2, [0]);
        BinaryPrimitives.WriteUInt64BigEndian(malformed.AsSpan(8, 8), 4UL << 33);
        Reject([], malformed, BuildBitmap(2, []));
    }

    [Fact]
    public void Index_split_decodes_ewah_zero_one_runs_and_multiple_running_words()
    {
        var sharedEntries = Enumerable.Range(0, 130).Select(index => new FixtureEntry($"file-{index:D3}")).ToArray();
        var shared = BuildIndex(2, sharedEntries);
        using var bitmap = new MemoryStream();
        WriteUInt32(bitmap, 130);
        WriteUInt32(bitmap, 4);
        WriteUInt64(bitmap, 3);
        WriteUInt64(bitmap, 2);
        WriteUInt64(bitmap, 1UL << 33);
        WriteUInt64(bitmap, 2);
        WriteUInt32(bitmap, 2);
        var split = BuildIndex(2, [], [("link", BuildLink(shared[^20..], bitmap.ToArray(), BuildBitmap(0, [])))]);
        var index = GitIndex.Parse(split, readSharedIndex: _ => shared);

        index.Entries.Count.Should().Be(65);
        index.IsTracked("file-000").Should().BeFalse();
        index.IsTracked("file-063").Should().BeFalse();
        index.IsTracked("file-064").Should().BeTrue();
        index.IsTracked("file-128").Should().BeTrue();
        index.IsTracked("file-129").Should().BeFalse();
    }

    private static TException Throws<TException>(Action action) where TException : Exception =>
        action.Should().ThrowExactly<TException>().Which;

    private sealed record FixtureEntry(
        string Path,
        uint Mode = 0x81a4,
        int Stage = 0,
        bool AssumeValid = false,
        ushort? ExtendedFlags = null,
        byte[]? RawPath = null,
        byte[]? V4Prefix = null,
        byte ObjectByte = 0x11);

    private static byte[] BuildIndex(
        int version,
        FixtureEntry[] entries,
        (string Signature, byte[] Content)[]? extensions = null,
        int objectIdLength = 20)
    {
        using var stream = new MemoryStream();
        stream.Write("DIRC"u8);
        WriteUInt32(stream, (uint)version);
        WriteUInt32(stream, (uint)entries.Length);
        byte[] previousPath = [];

        foreach (var entry in entries)
        {
            var start = stream.Position;
            stream.Write(new byte[24]);
            WriteUInt32(stream, entry.Mode);
            stream.Write(new byte[12]);
            stream.Write(Enumerable.Repeat(entry.ObjectByte, objectIdLength).ToArray());
            var path = entry.RawPath ?? Encoding.UTF8.GetBytes(entry.Path);
            var flags = (ushort)(Math.Min(path.Length, 0xfff) | (entry.Stage << 12) |
                (entry.AssumeValid ? 0x8000 : 0) | (entry.ExtendedFlags is not null ? 0x4000 : 0));
            Span<byte> flagBytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(flagBytes, flags);
            stream.Write(flagBytes);

            if (entry.ExtendedFlags is { } extendedFlags)
            {
                BinaryPrimitives.WriteUInt16BigEndian(flagBytes, extendedFlags);
                stream.Write(flagBytes);
            }

            var prefixLength = 0;

            if (version == 4)
            {
                while (prefixLength < Math.Min(previousPath.Length, path.Length) && previousPath[prefixLength] == path[prefixLength])
                    prefixLength++;

                stream.Write(entry.V4Prefix ?? EncodePathVarint(previousPath.Length - prefixLength));
            }

            stream.Write(path.AsSpan(prefixLength));
            stream.WriteByte(0);

            if (version != 4)
            {
                while ((stream.Position - start) % 8 != 0)
                    stream.WriteByte(0);
            }

            previousPath = path;
        }

        foreach (var (signature, content) in extensions ?? [])
        {
            stream.Write(Encoding.ASCII.GetBytes(signature));
            WriteUInt32(stream, (uint)content.Length);
            stream.Write(content);
        }

        return AppendChecksum(stream.ToArray(), objectIdLength);
    }

    private static byte[] EncodePathVarint(int value)
    {
        var bytes = new List<byte> { (byte)(value & 0x7f) };

        while ((value >>= 7) != 0)
        {
            value--;
            bytes.Add((byte)(0x80 | (value & 0x7f)));
        }

        bytes.Reverse();
        return bytes.ToArray();
    }

    private static byte[] BuildLink(byte[] checksum, byte[] deleted, byte[] replaced) =>
        [.. checksum, .. deleted, .. replaced];

    private static byte[] BuildBitmap(int bitCount, int[] selected)
    {
        using var stream = new MemoryStream();
        var wordCount = (bitCount + 63) / 64;
        WriteUInt32(stream, (uint)bitCount);
        WriteUInt32(stream, (uint)(wordCount + 1));
        WriteUInt64(stream, (ulong)wordCount << 33);

        for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
        {
            ulong word = 0;

            foreach (var bit in selected.Where(bit => bit / 64 == wordIndex))
                word |= 1UL << (bit % 64);

            WriteUInt64(stream, word);
        }

        WriteUInt32(stream, 0);
        return stream.ToArray();
    }

    private static byte[] AppendChecksum(byte[] content, int objectIdLength) =>
        [.. content, .. objectIdLength == 20 ? SHA1.HashData(content) : SHA256.HashData(content)];

    private static void Rehash(byte[] content, int objectIdLength = 20)
    {
        var checksum = objectIdLength == 20 ? SHA1.HashData(content.AsSpan(0, content.Length - objectIdLength)) :
            SHA256.HashData(content.AsSpan(0, content.Length - objectIdLength));
        checksum.CopyTo(content, content.Length - objectIdLength);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
