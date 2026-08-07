using AwesomeAssertions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace GitCore.UnitTests;

public class LocalGitRepositoryTests
{
    [Fact]
    public void Reads_an_object_beyond_four_gib_without_materializing_the_pack()
    {
        using var temporary = new TemporaryRepository();
        var content = "small object in a sparse pack"u8.ToArray();
        var objectId = ComputeObjectId(PackFile.ObjectType.Blob, content);
        var packedObject = EncodeRegularObject(PackFile.ObjectType.Blob, content);
        const long ObjectOffset = 4_294_967_308;
        var packPath = Path.Combine(temporary.PackDirectory, "pack-sparse.pack");

        using (var pack = new FileStream(packPath, FileMode.CreateNew, FileAccess.Write))
        {
            WritePackHeader(pack, 1);
            pack.Position = ObjectOffset;
            pack.Write(packedObject);
            pack.Write(new byte[20]);
        }

        File.WriteAllBytes(
            Path.ChangeExtension(packPath, ".idx"),
            BuildPackIndex([(objectId, ObjectOffset)]));

        var bytesRead = 0L;

        using var repository =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions
                {
                    Diagnostics =
                    diagnostic =>
                    {
                        if (diagnostic.Kind is GitDiagnosticEventKind.BytesRead)
                            bytesRead += diagnostic.ByteCount ?? 0;
                    }
                });

        var gitObject = repository.GetRequiredObject(objectId);

        gitObject.Type.Should().Be(PackFile.ObjectType.Blob);
        gitObject.Data.ToArray().Should().Equal(content);
        bytesRead.Should().BeLessThan(64 * 1024);
        new FileInfo(packPath).Length.Should().BeGreaterThan(4L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void Resolves_ofs_and_cross_pack_ref_deltas_through_random_access()
    {
        using var temporary = new TemporaryRepository();
        var baseContent = "hello"u8.ToArray();
        var firstResult = "hello world"u8.ToArray();
        var secondResult = "hello world!"u8.ToArray();
        var baseId = ComputeObjectId(PackFile.ObjectType.Blob, baseContent);
        var firstResultId = ComputeObjectId(PackFile.ObjectType.Blob, firstResult);
        var secondResultId = ComputeObjectId(PackFile.ObjectType.Blob, secondResult);
        var baseObject = EncodeRegularObject(PackFile.ObjectType.Blob, baseContent);
        const long BaseOffset = 12;
        var ofsOffset = BaseOffset + baseObject.Length;
        var ofsDelta = EncodeOfsDelta(ofsOffset - BaseOffset, CreateDelta(baseContent, firstResult));

        WritePack(
            temporary.PackDirectory,
            "pack-base",
            [baseObject, ofsDelta],
            [(baseId, BaseOffset), (firstResultId, ofsOffset)]);

        var refDelta = EncodeRefDelta(firstResultId, CreateDelta(firstResult, secondResult));

        WritePack(
            temporary.PackDirectory,
            "pack-ref",
            [refDelta],
            [(secondResultId, 12)]);

        using var repository = LocalGitRepository.Open(temporary.Path);

        repository.GetRequiredObject(firstResultId).Data.ToArray().Should().Equal(firstResult);
        repository.GetRequiredObject(secondResultId).Data.ToArray().Should().Equal(secondResult);
    }

    [Fact]
    public async Task Resolves_a_missing_ref_delta_base_with_an_async_provider()
    {
        using var temporary = new TemporaryRepository();
        var baseContent = "provider base"u8.ToArray();
        var resultContent = "provider base plus"u8.ToArray();
        var baseId = ComputeObjectId(PackFile.ObjectType.Blob, baseContent);
        var resultId = ComputeObjectId(PackFile.ObjectType.Blob, resultContent);
        var refDelta = EncodeRefDelta(baseId, CreateDelta(baseContent, resultContent));

        WritePack(
            temporary.PackDirectory,
            "pack-ref-only",
            [refDelta],
            [(resultId, 12)]);

        using var repository =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions
                {
                    MissingObjectPolicy = MissingObjectPolicy.Custom,
                    MissingObjectProvider =
                    async (request, cancellationToken) =>
                    {
                        await Task.Yield();
                        cancellationToken.ThrowIfCancellationRequested();

                        return
                            new GitObject(
                                PackFile.ObjectType.Blob,
                                baseContent.Length,
                                baseContent,
                                request.ObjectId);
                    }
                });

        var result = await repository.GetObjectAsync(resultId);

        result.Should().NotBeNull();
        result!.Data.ToArray().Should().Equal(resultContent);
    }

    [Fact]
    public async Task Traversal_prunes_before_loading_and_yields_lazy_blob_content()
    {
        using var temporary = new TemporaryRepository();
        var includedContent = "# Included"u8.ToArray();
        var includedId = WriteLooseObject(temporary.ObjectsDirectory, PackFile.ObjectType.Blob, includedContent);
        var missingTreeId = new string('a', 40);

        var rootTree =
            EncodeTree(
                ("40000", ".attachments", missingTreeId),
                ("100644", "Included.md", includedId));

        var rootTreeId = WriteLooseObject(temporary.ObjectsDirectory, PackFile.ObjectType.Tree, rootTree);
        var commitId = WriteCommit(temporary.ObjectsDirectory, rootTreeId);
        var diagnostics = new List<GitDiagnosticEvent>();

        using var repository =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions { Diagnostics = diagnostics.Add });

        var files =
            repository.EnumerateTreeAsync(
                commitId,
                new TreeTraversalOptions
                {
                    SelectFile =
                        file =>
                        {
                            file.ObjectId.Should().Be(includedId);
                            file.Mode.Should().Be("100644");

                            return
                            file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                            ?
                            TreeFileSelection.Include
                            :
                            TreeFileSelection.Skip;
                        },
                    SelectSubtree =
                        subtree =>
                        {
                            subtree.ObjectId.Should().Be(missingTreeId);

                            return
                            subtree.Path.Contains(".attachments", StringComparer.OrdinalIgnoreCase)
                            ?
                            TreeSubtreeSelection.Skip
                            :
                            TreeSubtreeSelection.Descend;
                        }
                });

        await using var enumerator = files.GetAsyncEnumerator();
        (await enumerator.MoveNextAsync()).Should().BeTrue();
        var file = enumerator.Current;
        file.Path.Should().Equal("Included.md");

        diagnostics.Should().NotContain(
            diagnostic =>
            diagnostic.Kind == GitDiagnosticEventKind.ObjectDecompressed &&
            diagnostic.ObjectId == includedId);

        await using var content = await file.OpenContentAsync();
        using var copied = new MemoryStream();
        await content.CopyToAsync(copied);

        copied.ToArray().Should().Equal(includedContent);

        diagnostics.Should().Contain(
            diagnostic =>
            diagnostic.Kind == GitDiagnosticEventKind.SkippedSubtree &&
            diagnostic.ObjectId == missingTreeId);

        repository.LookupObject(missingTreeId).Status.Should().Be(GitObjectLookupStatus.Missing);
    }

    [Fact]
    public void Creates_typed_tree_selectors_from_path_predicates()
    {
        var options =
            TreeTraversalOptions.FromPathPredicates(
                shouldIncludeFile: path => path[^1].EndsWith(".md", StringComparison.Ordinal),
                shouldDescend: path => path[^1] is not ".attachments");

        var fileSelection =
            options.SelectFile!(
                new TreeTraversalFile(
                    ["guide", "readme.md"],
                    "readme.md",
                    new string('a', 40),
                    "100644"));

        var subtreeSelection =
            options.SelectSubtree!(
                new TreeTraversalSubtree(
                    [".attachments"],
                    ".attachments",
                    new string('b', 40)));

        fileSelection.Should().Be(TreeFileSelection.Include);
        subtreeSelection.Should().Be(TreeSubtreeSelection.Skip);

        var optionsWithLimit =
            new TreeTraversalOptions
            {
                SelectFile =
                    TreeTraversalOptions.CreateFileSelector(
                        path => path[^1].EndsWith(".md", StringComparison.Ordinal)),
                MaximumTreeDepth = 1
            };

        optionsWithLimit.SelectFile(
            new TreeTraversalFile(
                ["readme.txt"],
                "readme.txt",
                new string('c', 40),
                "100644"))
        .Should()
        .Be(TreeFileSelection.Skip);
    }

    [Fact]
    public void Opens_linked_worktrees_and_resolves_shared_references()
    {
        using var temporary = new TemporaryRepository();
        var worktree = System.IO.Path.Combine(temporary.RootDirectory, "worktree");

        var worktreeGitDirectory =
            System.IO.Path.Combine(temporary.Path, "worktrees", "linked");

        Directory.CreateDirectory(worktree);
        Directory.CreateDirectory(worktreeGitDirectory);
        Directory.CreateDirectory(System.IO.Path.Combine(temporary.Path, "refs", "heads"));
        File.WriteAllText(System.IO.Path.Combine(worktree, ".git"), $"gitdir: {worktreeGitDirectory}");
        File.WriteAllText(System.IO.Path.Combine(worktreeGitDirectory, "commondir"), "../..");
        File.WriteAllText(System.IO.Path.Combine(worktreeGitDirectory, "HEAD"), "ref: refs/heads/main");
        var commitId = new string('b', 40);
        File.WriteAllText(System.IO.Path.Combine(temporary.Path, "refs", "heads", "main"), commitId);

        using var repository = LocalGitRepository.Open(worktree);

        repository.GitDirectory.Should().Be(System.IO.Path.GetFullPath(worktreeGitDirectory));
        repository.CommonGitDirectory.Should().Be(System.IO.Path.GetFullPath(temporary.Path));
        repository.ResolveHead().Should().Be(commitId);
    }

    [Fact]
    public void Follows_recursive_alternate_object_directories()
    {
        using var primary = new TemporaryRepository();
        using var alternate = new TemporaryRepository();
        var content = "from alternate"u8.ToArray();
        var objectId = WriteLooseObject(alternate.ObjectsDirectory, PackFile.ObjectType.Blob, content);
        var infoDirectory = System.IO.Path.Combine(primary.ObjectsDirectory, "info");
        Directory.CreateDirectory(infoDirectory);

        File.WriteAllText(
            System.IO.Path.Combine(infoDirectory, "alternates"),
            alternate.ObjectsDirectory);

        using var repository = LocalGitRepository.Open(primary.Path);

        repository.ObjectDirectories.Should().Contain(alternate.ObjectsDirectory);
        repository.GetRequiredObject(objectId).Data.ToArray().Should().Equal(content);
    }

    [Fact]
    public async Task Reports_promised_objects_and_uses_only_caller_supplied_retrieval()
    {
        using var temporary = new TemporaryRepository();

        File.WriteAllText(
            System.IO.Path.Combine(temporary.Path, "config"),
            """
            [extensions]
                partialClone = origin
            [remote "origin"]
                promisor = true
                partialclonefilter = blob:none
            """);

        var content = "provided"u8.ToArray();
        var objectId = ComputeObjectId(PackFile.ObjectType.Blob, content);
        using var localOnly = LocalGitRepository.Open(temporary.Path);
        var missing = localOnly.LookupObject(objectId);
        missing.Status.Should().Be(GitObjectLookupStatus.MissingPromised);
        missing.Error.Should().NotBeNull();
        missing.Error!.ObjectId.Should().Be(objectId);
        missing.Error.IsPromised.Should().BeTrue();
        missing.Error.PromisorRemotes.Should().Contain("origin");
        missing.Error.Context.RepositoryPath.Should().Be(temporary.Path);

        using var withProvider =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions
                {
                    MissingObjectPolicy = MissingObjectPolicy.FetchMissing,
                    MissingObjectProvider =
                    (request, cancellationToken) =>
                    ValueTask.FromResult<GitObject?>(
                        new GitObject(
                            PackFile.ObjectType.Blob,
                            content.Length,
                            content,
                            request.ObjectId))
                });

        var provided = await withProvider.GetObjectAsync(objectId);
        provided.Should().NotBeNull();
        provided!.Data.ToArray().Should().Equal(content);
    }

    [Fact]
    public void Enforces_materialized_object_size_with_typed_context()
    {
        using var temporary = new TemporaryRepository();

        var objectId =
            WriteLooseObject(
                temporary.ObjectsDirectory,
                PackFile.ObjectType.Blob,
                "too large"u8.ToArray());

        using var repository =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions { MaximumMaterializedObjectSize = 4 });

        var exception =
            ((Action)(() => repository.GetRequiredObject(objectId)))
            .Should()
            .Throw<GitObjectSizeLimitException>()
            .Which;

        exception.Context.ObjectId.Should().Be(objectId);
        exception.Context.ConfiguredLimit.Should().Be(4);
        exception.Context.ObservedValue.Should().Be(9);
    }

    [Fact]
    public async Task Honors_cancellation_before_traversal_or_provider_work()
    {
        using var temporary = new TemporaryRepository();
        var providerCalled = false;

        using var repository =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions
                {
                    MissingObjectPolicy = MissingObjectPolicy.Custom,
                    MissingObjectProvider =
                    (_, _) =>
                    {
                        providerCalled = true;
                        return ValueTask.FromResult<GitObject?>(null);
                    }
                });

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Func<Task> action =
            async () =>
            {
                await repository.GetObjectAsync(
                    new string('c', 40),
                    cancellation.Token);
            };

        await action.Should().ThrowAsync<OperationCanceledException>();

        providerCalled.Should().BeFalse();
    }

    [Fact]
    public async Task Propagates_cancellation_during_missing_object_retrieval()
    {
        using var temporary = new TemporaryRepository();
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();

        using var repository =
            LocalGitRepository.Open(
                temporary.Path,
                new LocalRepositoryOptions
                {
                    MissingObjectPolicy = MissingObjectPolicy.Custom,
                    MissingObjectProvider =
                    async (_, cancellationToken) =>
                    {
                        providerStarted.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return null;
                    }
                });

        var lookup = repository.GetObjectAsync(new string('e', 40), cancellation.Token).AsTask();
        await providerStarted.Task;
        await cancellation.CancelAsync();

        Func<Task> action = async () => await lookup;
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Synchronous_traversal_checks_cancellation_before_object_loading()
    {
        using var temporary = new TemporaryRepository();
        using var repository = LocalGitRepository.Open(temporary.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action action =
            () =>
            repository.EnumerateTree(
                new string('f', 40),
                cancellationToken: cancellation.Token)
            .GetEnumerator()
            .MoveNext();

        action.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Reference_resolution_does_not_open_unrelated_packs()
    {
        using var temporary = new TemporaryRepository();
        var commitId = new string('1', 40);
        var refsDirectory = System.IO.Path.Combine(temporary.Path, "refs", "heads");
        Directory.CreateDirectory(refsDirectory);
        File.WriteAllText(System.IO.Path.Combine(refsDirectory, "main"), commitId);
        File.WriteAllBytes(System.IO.Path.Combine(temporary.PackDirectory, "unindexed.pack"), [1, 2, 3]);

        LoadFromLocalFiles.ResolveHead(temporary.Path).Should().Be(commitId);
    }

    [Fact]
    public void Opening_fails_explicitly_for_an_unindexed_pack()
    {
        using var temporary = new TemporaryRepository();
        var packPath = System.IO.Path.Combine(temporary.PackDirectory, "unindexed.pack");
        File.WriteAllBytes(packPath, [1, 2, 3]);

        var exception =
            ((Action)(() => LocalGitRepository.Open(temporary.Path)))
            .Should()
            .Throw<InvalidPackIndexException>()
            .Which;

        exception.Context.PackPath.Should().Be(packPath);
        exception.Message.Should().Contain("no companion index");
    }

    [Fact]
    public void Opening_rejects_an_index_for_a_different_pack_checksum()
    {
        using var temporary = new TemporaryRepository();
        var content = "checksum"u8.ToArray();
        var objectId = ComputeObjectId(PackFile.ObjectType.Blob, content);

        WritePack(
            temporary.PackDirectory,
            "pack-checksum",
            [EncodeRegularObject(PackFile.ObjectType.Blob, content)],
            [(objectId, 12)]);

        var packPath = System.IO.Path.Combine(temporary.PackDirectory, "pack-checksum.pack");

        using (var pack = new FileStream(packPath, FileMode.Open, FileAccess.Write))
        {
            pack.Position = pack.Length - 1;
            pack.WriteByte(1);
        }

        var exception =
            ((Action)(() => LocalGitRepository.Open(temporary.Path)))
            .Should()
            .Throw<InvalidPackIndexException>()
            .Which;

        exception.Context.PackPath.Should().Be(packPath);
        exception.Message.Should().Contain("different pack checksum");
    }

    [Fact]
    public void Rejects_corrupt_index_bounds_with_index_context()
    {
        var index = BuildPackIndex([(new string('d', 40), 4_294_967_308)]);
        index[8 + 1024 + 20 + 4 + 3] = 1;
        RecomputeIndexChecksum(index);

        var exception =
            ((Action)(() => PackIndex.ParsePackIndexV2(index, "/repo/objects/pack/test.idx")))
            .Should()
            .Throw<InvalidPackIndexException>()
            .Which;

        exception.Context.IndexPath.Should().Be("/repo/objects/pack/test.idx");
        exception.Message.Should().Contain("inconsistent");
    }

    private static byte[] CreateDelta(byte[] baseContent, byte[] result)
    {
        var suffix = result.AsSpan(baseContent.Length).ToArray();

        return
            [
                checked((byte)baseContent.Length),
                checked((byte)result.Length),
                0x90,
                checked((byte)baseContent.Length),
                checked((byte)suffix.Length),
                .. suffix
            ];
    }

    private static byte[] EncodeRegularObject(
        PackFile.ObjectType objectType,
        byte[] content)
    {
        return [.. EncodePackObjectHeader(objectType, content.Length), .. Compress(content)];
    }

    private static byte[] EncodeOfsDelta(long distance, byte[] delta)
    {
        return
            [
                .. EncodePackObjectHeader(PackFile.ObjectType.OfsDelta, delta.Length),
                .. EncodeOfsDistance(distance),
                .. Compress(delta)
            ];
    }

    private static byte[] EncodeRefDelta(string baseObjectId, byte[] delta)
    {
        return
            [
                .. EncodePackObjectHeader(PackFile.ObjectType.RefDelta, delta.Length),
                .. Convert.FromHexString(baseObjectId),
                .. Compress(delta)
            ];
    }

    private static byte[] EncodePackObjectHeader(
        PackFile.ObjectType objectType,
        int size)
    {
        var bytes = new List<byte>();
        var remaining = size >> 4;
        var first = (byte)(((int)objectType << 4) | (size & 0x0F));

        if (remaining > 0)
            first |= 0x80;

        bytes.Add(first);

        while (remaining > 0)
        {
            var value = (byte)(remaining & 0x7F);
            remaining >>= 7;

            if (remaining > 0)
                value |= 0x80;

            bytes.Add(value);
        }

        return [.. bytes];
    }

    private static byte[] EncodeOfsDistance(long distance)
    {
        Span<byte> buffer = stackalloc byte[16];
        var index = buffer.Length;
        buffer[--index] = (byte)(distance & 0x7F);

        while ((distance >>= 7) > 0)
        {
            distance--;
            buffer[--index] = (byte)(0x80 | (distance & 0x7F));
        }

        return buffer[index..].ToArray();
    }

    private static byte[] Compress(ReadOnlySpan<byte> content)
    {
        using var output = new MemoryStream();

        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(content);

        return output.ToArray();
    }

    private static void WritePack(
        string packDirectory,
        string name,
        IReadOnlyList<byte[]> objects,
        IReadOnlyList<(string ObjectId, long Offset)> entries)
    {
        var packPath = System.IO.Path.Combine(packDirectory, name + ".pack");

        using (var pack = new FileStream(packPath, FileMode.CreateNew, FileAccess.Write))
        {
            WritePackHeader(pack, checked((uint)objects.Count));

            foreach (var packedObject in objects)
                pack.Write(packedObject);

            pack.Write(new byte[20]);
        }

        File.WriteAllBytes(
            System.IO.Path.ChangeExtension(packPath, ".idx"),
            BuildPackIndex(entries));
    }

    private static void WritePackHeader(Stream stream, uint objectCount)
    {
        Span<byte> header = stackalloc byte[12];
        "PACK"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..8], 2);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..12], objectCount);
        stream.Write(header);
    }

    private static byte[] BuildPackIndex(
        IReadOnlyList<(string ObjectId, long Offset)> entries)
    {
        var sorted = entries.OrderBy(entry => entry.ObjectId, StringComparer.Ordinal).ToArray();
        var largeOffsets = sorted.Where(entry => entry.Offset >= 0x80000000L).ToArray();

        var length =
            8 + 1024 + sorted.Length * 20 + sorted.Length * 4 + sorted.Length * 4 +
            largeOffsets.Length * 8 + 20 + 20;

        var data = new byte[length];
        var span = data.AsSpan();
        span[0] = 0xFF;
        span[1] = (byte)'t';
        span[2] = (byte)'O';
        span[3] = (byte)'c';
        BinaryPrimitives.WriteUInt32BigEndian(span[4..8], 2);
        var fanoutOffset = 8;

        for (var value = 0; value < 256; value++)
        {
            var count =
                sorted.Count(
                    entry => Convert.FromHexString(entry.ObjectId)[0] <= value);

            BinaryPrimitives.WriteUInt32BigEndian(
                span.Slice(fanoutOffset + value * 4, 4),
                checked((uint)count));
        }

        var shaOffset = fanoutOffset + 1024;
        var crcOffset = shaOffset + sorted.Length * 20;
        var offsetTableOffset = crcOffset + sorted.Length * 4;
        var largeOffsetTableOffset = offsetTableOffset + sorted.Length * 4;
        var nextLargeOffset = 0;

        for (var index = 0; index < sorted.Length; index++)
        {
            Convert.FromHexString(sorted[index].ObjectId)
                .CopyTo(span.Slice(shaOffset + index * 20, 20));

            if (sorted[index].Offset >= 0x80000000L)
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    span.Slice(offsetTableOffset + index * 4, 4),
                    0x80000000u | checked((uint)nextLargeOffset));

                BinaryPrimitives.WriteUInt64BigEndian(
                    span.Slice(largeOffsetTableOffset + nextLargeOffset * 8, 8),
                    checked((ulong)sorted[index].Offset));

                nextLargeOffset++;
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(
                    span.Slice(offsetTableOffset + index * 4, 4),
                    checked((uint)sorted[index].Offset));
            }
        }

        RecomputeIndexChecksum(data);
        return data;
    }

    private static void RecomputeIndexChecksum(byte[] index)
    {
        SHA1.HashData(index.AsSpan()[..^20]).CopyTo(index.AsSpan()[^20..]);
    }

    private static string WriteLooseObject(
        string objectsDirectory,
        PackFile.ObjectType objectType,
        byte[] content)
    {
        var objectId = ComputeObjectId(objectType, content);
        var header = Encoding.ASCII.GetBytes($"{ObjectTypeName(objectType)} {content.Length}\0");
        var looseContent = new byte[header.Length + content.Length];
        header.CopyTo(looseContent, 0);
        content.CopyTo(looseContent, header.Length);
        var objectDirectory = System.IO.Path.Combine(objectsDirectory, objectId[..2]);
        Directory.CreateDirectory(objectDirectory);

        File.WriteAllBytes(
            System.IO.Path.Combine(objectDirectory, objectId[2..]),
            Compress(looseContent));

        return objectId;
    }

    private static string WriteCommit(string objectsDirectory, string treeId)
    {
        var content =
            Encoding.UTF8.GetBytes(
                $"tree {treeId}\n" +
                "author Test <test@example.com> 0 +0000\n" +
                "committer Test <test@example.com> 0 +0000\n\n" +
                "test\n");

        return WriteLooseObject(objectsDirectory, PackFile.ObjectType.Commit, content);
    }

    private static byte[] EncodeTree(
        params (string Mode, string Name, string ObjectId)[] entries)
    {
        using var output = new MemoryStream();

        foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            output.Write(Encoding.UTF8.GetBytes($"{entry.Mode} {entry.Name}\0"));
            output.Write(Convert.FromHexString(entry.ObjectId));
        }

        return output.ToArray();
    }

    private static string ComputeObjectId(
        PackFile.ObjectType objectType,
        byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"{ObjectTypeName(objectType)} {content.Length}\0");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(header);
        hash.AppendData(content);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string ObjectTypeName(PackFile.ObjectType objectType)
    {
        return objectType.ToString().ToLowerInvariant();
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            RootDirectory =
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "gitcore-local-tests-" + Guid.NewGuid().ToString("N"));

            Path = System.IO.Path.Combine(RootDirectory, "repository.git");
            ObjectsDirectory = System.IO.Path.Combine(Path, "objects");
            PackDirectory = System.IO.Path.Combine(ObjectsDirectory, "pack");
            Directory.CreateDirectory(PackDirectory);
            File.WriteAllText(System.IO.Path.Combine(Path, "HEAD"), "ref: refs/heads/main");
        }

        public string RootDirectory { get; }

        public string Path { get; }

        public string ObjectsDirectory { get; }

        public string PackDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
    }
}
