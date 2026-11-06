using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace GitCore;

/// <summary>
/// Controls whether a Git staging-area index may omit its checksum.
/// Git can write an all-zero checksum field instead of calculating a checksum when
/// index.skipHash is enabled, including through feature.manyFiles.
/// Both policies validate nonzero checksums and the index structure.
/// Shared indexes must always contain a valid checksum matching their split-index reference.
/// </summary>
public enum GitIndexChecksumPolicy
{
    /// <summary>
    /// Requires a nonzero checksum matching the index contents. This is the default.
    /// Rejects Git's all-zero marker for an omitted checksum.
    /// </summary>
    RequireChecksum,

    /// <summary>
    /// Accepts Git's all-zero marker for an omitted checksum; otherwise verifies the checksum.
    /// Structural validation still applies, but content corruption cannot be detected by a checksum
    /// when the writer omitted it. Does not relax shared-index identity validation.
    /// </summary>
    ValidateIfPresent
}

/// <summary>
/// An immutable snapshot of a Git staging-area index, including all conflict stages.
/// </summary>
public sealed class GitIndex
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly HashSet<string> paths;

    private readonly HashSet<string> pathsIgnoringCase;

    private readonly string[] sparseDirectoryPaths;

    private GitIndex(List<GitIndexEntry> entries)
    {
        Entries = entries.AsReadOnly();
        paths = new HashSet<string>(entries.Select(entry => entry.Path), StringComparer.Ordinal);

        pathsIgnoringCase =
            new HashSet<string>(entries.Select(entry => entry.Path), GitPathComparison.IgnoreCaseComparer);

        sparseDirectoryPaths = entries.Where(entry => entry.IsSparseDirectory).Select(entry => entry.Path).ToArray();
    }

    /// <summary>
    /// The index entries, ordered by UTF-8 path bytes and then conflict stage.
    /// </summary>
    public IReadOnlyList<GitIndexEntry> Entries { get; }

    /// <summary>
    /// An empty staging area, for repositories without an index file.
    /// </summary>
    public static GitIndex Empty { get; } = new([]);

    /// <summary>
    /// Parses index versions 2, 3, and 4, validating their structure and applying the checksum policy.
    /// Split indexes are supported when a shared-index resolver is supplied.
    /// Sparse-directory entries are retained without expanding their trees.
    /// </summary>
    /// <param name="content">
    /// The complete index, including its trailing checksum field even when that field is all zero.
    /// </param>
    /// <param name="objectIdLength">20 for SHA-1 repositories, or 32 for SHA-256 repositories.</param>
    /// <param name="checksumPolicy">
    /// Defaults to <see cref="GitIndexChecksumPolicy.RequireChecksum"/>.
    /// Git can omit checksums by writing an all-zero field when index.skipHash is enabled,
    /// including through feature.manyFiles. Use <see cref="GitIndexChecksumPolicy.ValidateIfPresent"/>
    /// to accept that marker while still verifying every nonzero checksum.
    /// This policy is independent of the repository's current configuration.
    /// </param>
    /// <param name="readSharedIndex">
    /// Resolves a shared index by its lowercase hexadecimal checksum (without the sharedindex. prefix).
    /// Shared indexes must contain a valid checksum matching this identifier under either policy.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">The object identifier length or checksum policy is unsupported.</exception>
    /// <exception cref="InvalidDataException">
    /// The index or shared index is corrupt, or a required checksum was omitted.
    /// </exception>
    /// <exception cref="NotSupportedException">The index requires an unsupported version, extension, or unavailable shared index.</exception>
    public static GitIndex Parse(
        ReadOnlyMemory<byte> content,
        int objectIdLength = 20,
        GitIndexChecksumPolicy checksumPolicy = GitIndexChecksumPolicy.RequireChecksum,
        Func<string, ReadOnlyMemory<byte>>? readSharedIndex = null)
    {
        if (objectIdLength is not (20 or 32))
        {
            throw new ArgumentOutOfRangeException(
                nameof(objectIdLength),
                "Git object identifiers must contain 20 or 32 bytes.");
        }

        ValidateChecksumPolicy(checksumPolicy);

        return new GitIndex(ParseEntries(content, objectIdLength, checksumPolicy, readSharedIndex, isShared: false));
    }

    internal static void ValidateChecksumPolicy(GitIndexChecksumPolicy checksumPolicy)
    {
        if (!Enum.IsDefined(checksumPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(checksumPolicy),
                checksumPolicy,
                "Unsupported Git index checksum policy.");
        }
    }

    /// <summary>
    /// Tests exact index-path membership, optionally folding ASCII case only.
    /// Conflict stages, intent-to-add,
    /// assume-valid, and skip-worktree entries are all tracked.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The path is below a sparse directory whose tree has not been expanded.
    /// </exception>
    public bool IsTracked(string relativePath, bool ignoreCase = false)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if ((ignoreCase ? pathsIgnoringCase : paths).Contains(relativePath))
            return true;

        foreach (var sparseDirectoryPath in sparseDirectoryPaths)
        {
            if (GitPathComparison.StartsWith(relativePath, sparseDirectoryPath, ignoreCase))
            {
                throw new NotSupportedException(
                    $"Tracking below sparse index directory '{sparseDirectoryPath}' requires expanding its tree.");
            }
        }

        return false;
    }

    private static List<GitIndexEntry> ParseEntries(
        ReadOnlyMemory<byte> content,
        int objectIdLength,
        GitIndexChecksumPolicy checksumPolicy,
        Func<string, ReadOnlyMemory<byte>>? readSharedIndex,
        bool isShared)
    {
        var span = content.Span;

        if (span.Length < 12 + objectIdLength)
            throw new InvalidDataException("Git index header or checksum is truncated.");

        if (!span[..4].SequenceEqual("DIRC"u8))
            throw new InvalidDataException("Invalid Git index signature.");

        var checksumOffset = span.Length - objectIdLength;
        var storedChecksum = span[checksumOffset..];

        if (storedChecksum.IndexOfAnyExcept((byte)0) < 0)
        {
            if (checksumPolicy is GitIndexChecksumPolicy.RequireChecksum)
            {
                throw new InvalidDataException(
                    isShared
                    ?
                    "Shared Git index checksum is omitted (all-zero checksum field). " +
                    "Shared indexes must contain a valid checksum matching their split-index reference, " +
                    "even under GitIndexChecksumPolicy.ValidateIfPresent."
                    :
                    "Git index checksum is omitted (all-zero checksum field). " +
                    "Git can omit index checksums when index.skipHash is enabled, including through feature.manyFiles. " +
                    "To allow omission while still validating nonzero checksums, pass " +
                    "checksumPolicy: GitIndexChecksumPolicy.ValidateIfPresent to GitIndex.Parse, or set " +
                    "GitWorkingTreeOptions.IndexChecksumPolicy to GitIndexChecksumPolicy.ValidateIfPresent.");
            }
        }
        else
        {
            var checksum =
                objectIdLength == 20 ? SHA1.HashData(span[..checksumOffset]) : SHA256.HashData(span[..checksumOffset]);

            if (!storedChecksum.SequenceEqual(checksum))
                throw new InvalidDataException("Git index checksum does not match its content.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(4, 4));

        if (version is not (2 or 3 or 4))
            throw new NotSupportedException($"Git index version {version} is not supported.");

        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(8, 4));
        var fixedLength = 42 + objectIdLength;

        if (entryCount > (uint)((checksumOffset - 12) / (fixedLength + 1)))
            throw new InvalidDataException("Git index entry count exceeds the available data.");

        var entries = new List<GitIndexEntry>((int)entryCount);
        var offset = 12;
        byte[] previousPath = [];

        for (var index = 0; index < entryCount; index++)
        {
            var entryStart = offset;
            RequireBytes(offset, fixedLength, checksumOffset);
            var mode = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 24, 4));
            var objectId = Convert.ToHexStringLower(span.Slice(offset + 40, objectIdLength));
            var flags = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset + 40 + objectIdLength, 2));
            offset += fixedLength;
            ushort extendedFlags = 0;

            if ((flags & 0x4000) != 0)
            {
                if (version == 2)
                    throw new InvalidDataException("Version 2 Git index entries cannot contain extended flags.");

                RequireBytes(offset, 2, checksumOffset);
                extendedFlags = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2));
                offset += 2;

                if ((extendedFlags & ~0x6000) != 0)
                    throw new InvalidDataException("Git index entry contains reserved extended flags.");
            }

            var prefixLength = 0;

            if (version == 4)
            {
                var removeLength = ReadPathVarint(span, ref offset, checksumOffset);

                if (removeLength > previousPath.Length)
                {
                    throw new InvalidDataException(
                        "Version 4 Git index path removes more bytes than its predecessor contains.");
                }

                prefixLength = previousPath.Length - removeLength;
            }

            var suffixLength = span.Slice(offset, checksumOffset - offset).IndexOf((byte)0);

            if (suffixLength < 0)
                throw new InvalidDataException("Git index path is not terminated.");

            var pathBytes = new byte[prefixLength + suffixLength];
            previousPath.AsSpan(0, prefixLength).CopyTo(pathBytes);
            span.Slice(offset, suffixLength).CopyTo(pathBytes.AsSpan(prefixLength));
            offset += suffixLength + 1;

            if ((flags & 0x0fff) != Math.Min(pathBytes.Length, 0x0fff))
                throw new InvalidDataException("Git index path length does not match its flags.");

            string path;

            try
            {
                path = StrictUtf8.GetString(pathBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("Git index path is not valid UTF-8.", exception);
            }

            previousPath = pathBytes;

            if (version != 4)
            {
                var padding = (8 - ((offset - entryStart) % 8)) % 8;
                RequireBytes(offset, padding, checksumOffset);

                foreach (var paddingByte in span.Slice(offset, padding))
                {
                    if (paddingByte != 0)
                        throw new InvalidDataException("Git index entry padding must be zero.");
                }

                offset += padding;
            }

            entries.Add(
                new GitIndexEntry(
                    path,
                    mode,
                    (flags >> 12) & 3,
                    (extendedFlags & 0x4000) != 0,
                    (flags & 0x8000) != 0,
                    objectId));
        }

        ReadOnlyMemory<byte>? link = null;
        var sparse = false;

        while (offset < checksumOffset)
        {
            RequireBytes(offset, 8, checksumOffset);
            var signatureBytes = span.Slice(offset, 4);
            var signature = Encoding.ASCII.GetString(signatureBytes);
            var size = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 4, 4));
            offset += 8;

            if (size > checksumOffset - offset)
                throw new InvalidDataException($"Git index extension '{signature}' is truncated.");

            if (signature == "link")
            {
                if (link is not null || isShared)
                    throw new InvalidDataException("Git index contains a duplicate or nested split-index link.");

                link = content.Slice(offset, (int)size);
            }
            else if (signature == "sdir")
            {
                if (sparse || size != 0)
                    throw new InvalidDataException("Git sparse-index extension is invalid.");

                sparse = true;
            }
            else if (signatureBytes[0] is >= (byte)'a' and <= (byte)'z')
            {
                throw new NotSupportedException($"Mandatory Git index extension '{signature}' is not supported.");
            }
            else if (signatureBytes[0] is not (>= (byte)'A' and <= (byte)'Z'))
            {
                throw new InvalidDataException("Git index extension signature is invalid.");
            }

            offset += (int)size;
        }

        if (link is { } linkContent)
            entries = MergeSplitIndex(entries, linkContent.Span, objectIdLength, readSharedIndex);

        ValidateEntries(entries, sparse || link is not null);
        return entries;
    }

    private static List<GitIndexEntry> MergeSplitIndex(
        List<GitIndexEntry> entries,
        ReadOnlySpan<byte> link,
        int objectIdLength,
        Func<string, ReadOnlyMemory<byte>>? readSharedIndex)
    {
        RequireBytes(0, objectIdLength, link.Length);
        var sharedIdBytes = link[..objectIdLength];
        var sharedId = Convert.ToHexStringLower(sharedIdBytes);
        var sharedEntries = new List<GitIndexEntry>();

        if (sharedIdBytes.IndexOfAnyExcept((byte)0) >= 0)
        {
            if (readSharedIndex is null)
                throw new NotSupportedException($"Split Git index requires a resolver for shared index '{sharedId}'.");

            var shared = readSharedIndex(sharedId);

            sharedEntries =
                ParseEntries(
                    shared,
                    objectIdLength,
                    GitIndexChecksumPolicy.RequireChecksum,
                    readSharedIndex: null,
                    isShared: true);

            if (!shared.Span[^objectIdLength..].SequenceEqual(sharedIdBytes))
                throw new InvalidDataException("Shared Git index checksum differs from the split-index link.");
        }

        var offset = objectIdLength;
        var deleted = new HashSet<int>();
        var replaced = new HashSet<int>();

        if (offset < link.Length)
        {
            deleted = ReadBitmap(link, ref offset, sharedEntries.Count);
            replaced = ReadBitmap(link, ref offset, sharedEntries.Count);
        }

        if (offset != link.Length || deleted.Overlaps(replaced) || replaced.Count > entries.Count)
            throw new InvalidDataException("Split Git index delete/replace bitmaps are inconsistent.");

        var merged = new List<GitIndexEntry>();
        var replacementIndex = 0;

        for (var index = 0; index < sharedEntries.Count; index++)
        {
            if (deleted.Contains(index))
                continue;

            if (replaced.Contains(index))
            {
                var replacement = entries[replacementIndex++];

                if (replacement.Path.Length != 0)
                    throw new InvalidDataException("Split Git index replacement entries must have empty paths.");

                merged.Add(
                    new GitIndexEntry(
                        sharedEntries[index].Path,
                        replacement.Mode,
                        replacement.Stage,
                        replacement.SkipWorktree,
                        replacement.AssumeValid,
                        replacement.ObjectId));
            }
            else
            {
                merged.Add(sharedEntries[index]);
            }
        }

        for (var index = replacementIndex; index < entries.Count; index++)
        {
            if (entries[index].Path.Length == 0)
                throw new InvalidDataException("Split Git index new entries must have nonempty paths.");

            merged.Add(entries[index]);
        }

        merged.Sort(CompareEntries);
        return merged;
    }

    private static HashSet<int> ReadBitmap(ReadOnlySpan<byte> span, ref int offset, int entryCount)
    {
        RequireBytes(offset, 8, span.Length);
        var bitCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
        var wordCount = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset + 4, 4));
        offset += 8;

        if (bitCount > entryCount || wordCount == 0 || wordCount > (span.Length - offset - 4L) / 8)
            throw new InvalidDataException("Split Git index EWAH bitmap bounds are invalid.");

        var words = span.Slice(offset, (int)wordCount * 8);
        offset += (int)wordCount * 8;
        RequireBytes(offset, 4, span.Length);
        var runningWordPosition = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4));
        offset += 4;
        var selected = new HashSet<int>();
        var compressedPosition = 0;
        var lastRunningWord = 0;
        ulong expandedWords = 0;
        var expectedWords = ((ulong)bitCount + 63) / 64;

        while (compressedPosition < wordCount)
        {
            lastRunningWord = compressedPosition;
            var runningWord = BinaryPrimitives.ReadUInt64BigEndian(words.Slice(compressedPosition * 8, 8));
            compressedPosition++;
            var runLength = (runningWord >> 1) & 0xffffffff;
            var literalCount = runningWord >> 33;

            if (runLength + literalCount > expectedWords - expandedWords ||
                literalCount > (ulong)wordCount - (ulong)compressedPosition)
            {
                throw new InvalidDataException("Split Git index EWAH run exceeds its bounds.");
            }

            if ((runningWord & 1) != 0)
            {
                for (ulong word = 0; word < runLength; word++)
                    AddBitmapWord(ulong.MaxValue, expandedWords + word, bitCount, selected);
            }

            expandedWords += runLength;

            for (ulong literal = 0; literal < literalCount; literal++)
            {
                var value = BinaryPrimitives.ReadUInt64BigEndian(words.Slice(compressedPosition * 8, 8));
                AddBitmapWord(value, expandedWords++, bitCount, selected);
                compressedPosition++;
            }
        }

        if (expandedWords != expectedWords || runningWordPosition != lastRunningWord)
            throw new InvalidDataException("Split Git index EWAH bitmap length or running-word position is invalid.");

        return selected;
    }

    private static void AddBitmapWord(ulong word, ulong wordIndex, uint bitCount, HashSet<int> selected)
    {
        for (var bit = 0; bit < 64; bit++)
        {
            if ((word & (1UL << bit)) == 0)
                continue;

            var position = wordIndex * 64 + (ulong)bit;

            if (position >= bitCount)
                throw new InvalidDataException("Split Git index EWAH bitmap sets an out-of-range bit.");

            selected.Add((int)position);
        }
    }

    private static int ReadPathVarint(ReadOnlySpan<byte> span, ref int offset, int end)
    {
        RequireBytes(offset, 1, end);
        var valueByte = span[offset++];
        long value = valueByte & 0x7f;

        while ((valueByte & 0x80) != 0)
        {
            RequireBytes(offset, 1, end);
            valueByte = span[offset++];
            value = ((value + 1) << 7) + (valueByte & 0x7f);

            if (value > int.MaxValue)
                throw new InvalidDataException("Version 4 Git index path compression integer is too large.");
        }

        return (int)value;
    }

    private static void ValidateEntries(List<GitIndexEntry> entries, bool allowSparse)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var path = entry.Path;

            if (entry.IsSparseDirectory)
            {
                if (!allowSparse || !entry.SkipWorktree || entry.Stage != 0 ||
                    !path.EndsWith("/", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Git sparse-directory entry is invalid or lacks the sparse-index extension.");
                }

                path = path[..^1];
            }
            else if (entry.Mode is not (0x81a4 or 0x81ed or 0xa000 or 0xe000))
            {
                throw new InvalidDataException($"Git index entry '{path}' has an invalid mode.");
            }

            if (path.Length == 0 || path.Split('/').Any(component => component.Length == 0 || component is "." or ".."))
                throw new InvalidDataException("Git index path must be a nonempty repository-relative path.");

            if (index > 0 && CompareEntries(entries[index - 1], entry) >= 0)
                throw new InvalidDataException("Git index entries are duplicated or not ordered by path and stage.");
        }
    }

    private static int CompareEntries(GitIndexEntry left, GitIndexEntry right)
    {
        var pathComparison = StrictUtf8.GetBytes(left.Path).AsSpan().SequenceCompareTo(StrictUtf8.GetBytes(right.Path));
        return pathComparison != 0 ? pathComparison : left.Stage.CompareTo(right.Stage);
    }

    private static void RequireBytes(int offset, int length, int end)
    {
        if (offset < 0 || length < 0 || offset > end || length > end - offset)
            throw new InvalidDataException("Git index data is truncated.");
    }
}

/// <summary>
/// Immutable staging-area metadata for one path and conflict stage.
/// </summary>
public sealed class GitIndexEntry
{
    internal GitIndexEntry(string path, uint mode, int stage, bool skipWorktree, bool assumeValid, string objectId)
    {
        Path = path;
        Mode = mode;
        Stage = stage;
        SkipWorktree = skipWorktree;
        AssumeValid = assumeValid;
        ObjectId = objectId;
    }

    /// <summary>The repository-relative, slash-separated path, with a trailing slash for sparse directories.</summary>
    public string Path { get; }

    /// <summary>The Git file mode, including its object-type bits.</summary>
    public uint Mode { get; }

    /// <summary>Zero for normal entries, or one through three for conflict entries.</summary>
    public int Stage { get; }

    /// <summary>Whether Git should avoid updating this working-tree path.</summary>
    public bool SkipWorktree { get; }

    /// <summary>Whether Git assumes the working-tree file is unchanged.</summary>
    public bool AssumeValid { get; }

    /// <summary>The lowercase hexadecimal object identifier.</summary>
    public string ObjectId { get; }

    /// <summary>Whether this entry represents an unexpanded sparse directory tree.</summary>
    public bool IsSparseDirectory => Mode == 0x4000;
}
