using Microsoft.Win32.SafeHandles;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitCore;

/// <summary>
/// Provides bounded, indexed, random-access reads from a local Git object database.
/// </summary>
public sealed class LocalGitRepository : IGitObjectDatabase
{
    private readonly LocalRepositoryOptions _options;

    private readonly Dictionary<string, ObjectLocation> _locations = new(StringComparer.Ordinal);

    private readonly List<PackInfo> _packs = [];

    private readonly Lock _cacheLock = new();

    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cache = new(StringComparer.Ordinal);

    private readonly LinkedList<CacheEntry> _cacheLru = [];

    private long _cachedBytes;

    private bool _disposed;

    private LocalGitRepository(
        RepositoryLayout layout,
        LocalRepositoryOptions options,
        IReadOnlyList<string> promisorRemotes,
        IReadOnlyList<string> partialCloneFilters)
    {
        RepositoryPath = layout.RepositoryPath;
        GitDirectory = layout.GitDirectory;
        CommonGitDirectory = layout.CommonGitDirectory;
        ObjectDirectories = layout.ObjectDirectories;
        PromisorRemotes = promisorRemotes;
        PartialCloneFilters = partialCloneFilters;
        IsPartialClone = promisorRemotes.Count > 0;
        _options = options;

        try
        {
            IndexObjectStores();
        }
        catch
        {
            Dispose();
            throw;
        }

        Report(
            new GitDiagnosticEvent(
                GitDiagnosticEventKind.RepositoryOpened,
                RepositoryPath,
                "Open"));
    }

    /// <summary>
    /// Gets the original worktree, Git directory, or bare repository path supplied to <see cref="Open"/>.
    /// </summary>
    public string RepositoryPath { get; }

    /// <summary>
    /// Gets the per-worktree Git directory.
    /// </summary>
    public string GitDirectory { get; }

    /// <summary>
    /// Gets the common Git directory containing shared objects and references.
    /// </summary>
    public string CommonGitDirectory { get; }

    /// <summary>
    /// Gets the primary and alternate object directories in lookup order.
    /// </summary>
    public IReadOnlyList<string> ObjectDirectories { get; }

    /// <summary>
    /// Gets whether repository configuration declares at least one promisor remote.
    /// </summary>
    public bool IsPartialClone { get; }

    /// <summary>
    /// Gets the configured promisor remote names.
    /// </summary>
    public IReadOnlyList<string> PromisorRemotes { get; }

    /// <summary>
    /// Gets the partial clone filters declared by promisor remotes.
    /// </summary>
    public IReadOnlyList<string> PartialCloneFilters { get; }

    /// <summary>
    /// Gets the object identifiers indexed when the database was opened.
    /// </summary>
    public IReadOnlyCollection<string> ObjectIds =>
        new ReadOnlyCollection<string>([.. _locations.Keys.Order(StringComparer.Ordinal)]);

    /// <summary>
    /// Opens a worktree, a .git directory or file, or a bare repository.
    /// </summary>
    public static LocalGitRepository Open(
        string repositoryPath,
        LocalRepositoryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        options ??= new LocalRepositoryOptions();
        ValidateOptions(options);

        var layout = DiscoverLayout(repositoryPath);
        var (promisorRemotes, partialCloneFilters) = ReadPartialCloneConfiguration(layout);
        return new LocalGitRepository(layout, options, promisorRemotes, partialCloneFilters);
    }

    /// <summary>
    /// Gets an object when it is available under the configured policy.
    /// </summary>
    public GitObject? GetObject(string objectId)
    {
        return LookupObject(objectId).Object;
    }

    /// <summary>
    /// Asynchronously gets an object when it is available under the configured policy.
    /// </summary>
    public async ValueTask<GitObject?> GetObjectAsync(
        string objectId,
        CancellationToken cancellationToken = default)
    {
        return (await LookupObjectAsync(objectId, cancellationToken).ConfigureAwait(false)).Object;
    }

    /// <summary>
    /// Looks up an object and reports explicit missing-object information.
    /// </summary>
    public GitObjectLookupResult LookupObject(string objectId)
    {
        return LookupObjectSynchronously(objectId, CancellationToken.None);
    }

    /// <summary>
    /// Asynchronously looks up an object and reports explicit missing-object information.
    /// </summary>
    public async ValueTask<GitObjectLookupResult> LookupObjectAsync(
        string objectId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedObjectId = NormalizeObjectId(objectId);
        var context = new ResolutionContext();
        GitObject? localObject;

        while (true)
        {
            try
            {
                localObject =
                    ResolveLocalObject(
                        normalizedObjectId,
                        context,
                        deltaDepth: 0,
                        cancellationToken);

                break;
            }
            catch (MissingDeltaBaseException missingBase)
            {
                var providedBase =
                    await RequestMissingDeltaBaseAsync(
                        missingBase.ObjectId,
                        cancellationToken)
                    .ConfigureAwait(false);

                context.ProvidedObjects[missingBase.ObjectId] = providedBase;
            }
        }

        if (localObject is not null)
        {
            return
                new GitObjectLookupResult(
                    normalizedObjectId,
                    GitObjectLookupStatus.Found,
                    localObject,
                    null);
        }

        var isPromised = IsPartialClone;

        var shouldRequest =
            _options.MissingObjectProvider is not null &&
            (_options.MissingObjectPolicy is MissingObjectPolicy.Custom ||
            (_options.MissingObjectPolicy is MissingObjectPolicy.FetchMissing && isPromised));

        Exception? providerException = null;

        if (shouldRequest)
        {
            Report(
                new GitDiagnosticEvent(
                    GitDiagnosticEventKind.MissingObjectRequested,
                    RepositoryPath,
                    "GetObject",
                    normalizedObjectId));

            try
            {
                var provided =
                    await _options.MissingObjectProvider!(
                        new MissingGitObjectRequest(
                            RepositoryPath,
                            normalizedObjectId,
                            isPromised,
                            PromisorRemotes,
                            PartialCloneFilters),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (provided is not null)
                {
                    ValidateProvidedObject(normalizedObjectId, provided);
                    AddToCache(provided);

                    return
                        new GitObjectLookupResult(
                            normalizedObjectId,
                            GitObjectLookupStatus.Found,
                            provided,
                            null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                providerException = exception;
            }
        }

        var error =
            new GitObjectNotFoundException(
                normalizedObjectId,
                CreateErrorContext(
                    "GetObject",
                    objectId: normalizedObjectId,
                    isPartialClone: isPromised),
                providerException);

        Report(
            new GitDiagnosticEvent(
                GitDiagnosticEventKind.MissingObject,
                RepositoryPath,
                "GetObject",
                normalizedObjectId));

        return
            new GitObjectLookupResult(
                normalizedObjectId,
                isPromised ? GitObjectLookupStatus.MissingPromised : GitObjectLookupStatus.Missing,
                null,
                error);
    }

    private GitObjectLookupResult LookupObjectSynchronously(
        string objectId,
        CancellationToken cancellationToken)
    {
        if (_options.MissingObjectProvider is null)
        {
            return
                LookupObjectAsync(objectId, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        }

        return
            Task.Run(
                async () =>
                await LookupObjectAsync(objectId, cancellationToken).ConfigureAwait(false),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    private async ValueTask<GitObject> RequestMissingDeltaBaseAsync(
        string objectId,
        CancellationToken cancellationToken)
    {
        var shouldRequest =
            _options.MissingObjectProvider is not null &&
            (_options.MissingObjectPolicy is MissingObjectPolicy.Custom ||
            (_options.MissingObjectPolicy is MissingObjectPolicy.FetchMissing && IsPartialClone));

        if (!shouldRequest)
        {
            throw new GitObjectNotFoundException(
                objectId,
                CreateErrorContext(
                    "ResolveRefDelta",
                    objectId: objectId,
                    isPartialClone: IsPartialClone));
        }

        Report(
            new GitDiagnosticEvent(
                GitDiagnosticEventKind.MissingObjectRequested,
                RepositoryPath,
                "ResolveRefDelta",
                objectId));

        GitObject? provided;

        try
        {
            provided =
                await _options.MissingObjectProvider!(
                    new MissingGitObjectRequest(
                        RepositoryPath,
                        objectId,
                        IsPartialClone,
                        PromisorRemotes,
                        PartialCloneFilters),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new GitObjectNotFoundException(
                objectId,
                CreateErrorContext(
                    "ResolveRefDelta",
                    objectId: objectId,
                    isPartialClone: IsPartialClone),
                exception);
        }

        if (provided is null)
        {
            throw new GitObjectNotFoundException(
                objectId,
                CreateErrorContext(
                    "ResolveRefDelta",
                    objectId: objectId,
                    isPartialClone: IsPartialClone));
        }

        ValidateProvidedObject(objectId, provided);
        AddToCache(provided);
        return provided;
    }

    /// <summary>
    /// Gets an object or throws a context-rich <see cref="GitObjectNotFoundException"/>.
    /// </summary>
    public GitObject GetRequiredObject(string objectId)
    {
        return GetRequiredObject(objectId, CancellationToken.None);
    }

    /// <summary>
    /// Gets an object or throws a context-rich <see cref="GitObjectNotFoundException"/>.
    /// </summary>
    public GitObject GetRequiredObject(
        string objectId,
        CancellationToken cancellationToken)
    {
        var result = LookupObjectSynchronously(objectId, cancellationToken);
        return result.Object ?? throw result.Error!;
    }

    /// <summary>
    /// Gets an object asynchronously or throws a context-rich <see cref="GitObjectNotFoundException"/>.
    /// </summary>
    public async ValueTask<GitObject> GetRequiredObjectAsync(
        string objectId,
        CancellationToken cancellationToken = default)
    {
        var result = await LookupObjectAsync(objectId, cancellationToken).ConfigureAwait(false);
        return result.Object ?? throw result.Error!;
    }

    /// <summary>
    /// Resolves HEAD using the per-worktree and common reference stores.
    /// </summary>
    public string? ResolveHead()
    {
        return ResolveReference("HEAD");
    }

    /// <summary>
    /// Resolves HEAD without indexing or opening the repository's object packs.
    /// </summary>
    public static string? ResolveHeadAtPath(string repositoryPath)
    {
        return ResolveReferenceAtPath(repositoryPath, "HEAD");
    }

    /// <summary>
    /// Resolves a reference without indexing or opening the repository's object packs.
    /// </summary>
    public static string? ResolveReferenceAtPath(
        string repositoryPath,
        string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return ResolveReferenceFromLayout(DiscoverLayout(repositoryPath), reference);
    }

    /// <summary>
    /// Resolves a loose, symbolic, or packed reference with cycle detection.
    /// </summary>
    public string? ResolveReference(string reference)
    {
        ThrowIfDisposed();

        return
            ResolveReferenceFromLayout(
                new RepositoryLayout(
                    RepositoryPath,
                    GitDirectory,
                    CommonGitDirectory,
                    ObjectDirectories),
                reference);
    }

    /// <summary>
    /// Enumerates selected files without resolving their blobs until content is opened.
    /// </summary>
    public IEnumerable<GitTreeFile> EnumerateTree(
        string commitId,
        TreeTraversalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        options ??= new TreeTraversalOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedCommitId = NormalizeObjectId(commitId);
        var commitObject = GetRequiredObject(normalizedCommitId, cancellationToken);

        if (commitObject.Type is not PackFile.ObjectType.Commit)
        {
            throw new GitRepositoryException(
                $"Object {normalizedCommitId} is {commitObject.Type}, not a commit.",
                CreateErrorContext(
                    "EnumerateTree",
                    objectId: normalizedCommitId,
                    commitId: normalizedCommitId,
                    objectType: commitObject.Type));
        }

        GitObjects.CommitObject commit;

        try
        {
            commit = GitObjects.ParseCommit(commitObject.Data);
        }
        catch (Exception exception)
        {
            throw new GitRepositoryException(
                $"Commit {normalizedCommitId} could not be parsed.",
                CreateErrorContext(
                    "EnumerateTree",
                    objectId: normalizedCommitId,
                    commitId: normalizedCommitId,
                    objectType: commitObject.Type),
                exception);
        }

        var state = CreateTraversalState(options);
        return TraverseTree(commit.TreeHash, [], depth: 0, normalizedCommitId, options, state, cancellationToken);
    }

    /// <summary>
    /// Asynchronously enumerates selected files without resolving their blobs until content is opened.
    /// </summary>
    public async IAsyncEnumerable<GitTreeFile> EnumerateTreeAsync(
        string commitId,
        TreeTraversalOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        options ??= new TreeTraversalOptions();
        var normalizedCommitId = NormalizeObjectId(commitId);
        var commitObject = await GetRequiredObjectAsync(normalizedCommitId, cancellationToken).ConfigureAwait(false);

        if (commitObject.Type is not PackFile.ObjectType.Commit)
        {
            throw new GitRepositoryException(
                $"Object {normalizedCommitId} is {commitObject.Type}, not a commit.",
                CreateErrorContext(
                    "EnumerateTreeAsync",
                    objectId: normalizedCommitId,
                    commitId: normalizedCommitId,
                    objectType: commitObject.Type));
        }

        GitObjects.CommitObject commit;

        try
        {
            commit = GitObjects.ParseCommit(commitObject.Data);
        }
        catch (Exception exception)
        {
            throw new GitRepositoryException(
                $"Commit {normalizedCommitId} could not be parsed.",
                CreateErrorContext(
                    "EnumerateTreeAsync",
                    objectId: normalizedCommitId,
                    commitId: normalizedCommitId,
                    objectType: commitObject.Type),
                exception);
        }

        var state = CreateTraversalState(options);

        await foreach (var file in TraverseTreeAsync(
            commit.TreeHash,
            [],
            depth: 0,
            normalizedCommitId,
            options,
            state,
            cancellationToken))
        {
            yield return file;
        }
    }

    /// <summary>
    /// Copies a resolved blob to a caller-owned stream.
    /// Oversized delta objects fail with a typed size-limit exception rather than overflowing an array.
    /// </summary>
    public async ValueTask CopyBlobToAsync(
        string objectId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var gitObject = await GetRequiredObjectAsync(objectId, cancellationToken).ConfigureAwait(false);

        if (gitObject.Type is not PackFile.ObjectType.Blob)
        {
            throw new GitRepositoryException(
                $"Object {gitObject.ObjectId} is {gitObject.Type}, not a blob.",
                CreateErrorContext(
                    "CopyBlobToAsync",
                    objectId: gitObject.ObjectId,
                    objectType: gitObject.Type,
                    expectedSize: gitObject.Size));
        }

        await destination.WriteAsync(gitObject.Data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases pack files and cached objects owned by the repository.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var pack in _packs)
            pack.Dispose();

        _packs.Clear();

        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheLru.Clear();
            _cachedBytes = 0;
        }
    }

    private static void ValidateOptions(LocalRepositoryOptions options)
    {
        if (options.MaximumCachedObjectCount is < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumCachedObjectCount));

        if (options.MaximumCachedObjectBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumCachedObjectBytes));

        if (options.MaximumMaterializedObjectSize < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumMaterializedObjectSize));

        if (options.MaximumDeltaChainDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumDeltaChainDepth));

        if (options.MaximumTreeDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumTreeDepth));

        if (options.MaximumSelectedEntryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumSelectedEntryCount));

        if (options.MissingObjectPolicy is not MissingObjectPolicy.LocalOnly &&
            options.MissingObjectProvider is null)
        {
            throw new ArgumentException(
                "A MissingObjectProvider is required when MissingObjectPolicy permits retrieval.",
                nameof(options));
        }
    }

    private void IndexObjectStores()
    {
        foreach (var objectDirectory in ObjectDirectories)
        {
            IndexLooseObjects(objectDirectory);
            IndexPacks(objectDirectory);
        }
    }

    private void IndexLooseObjects(string objectDirectory)
    {
        if (!Directory.Exists(objectDirectory))
            return;

        foreach (var directory in Directory.EnumerateDirectories(objectDirectory))
        {
            var directoryName = Path.GetFileName(directory);

            if (directoryName.Length is not 2 || !directoryName.All(IsLowerHex))
                continue;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var fileName = Path.GetFileName(file);

                if (fileName.Length is not 38 || !fileName.All(IsLowerHex))
                    continue;

                _locations.TryAdd(directoryName + fileName, new LooseObjectLocation(file));
            }
        }
    }

    private void IndexPacks(string objectDirectory)
    {
        var packDirectory = Path.Combine(objectDirectory, "pack");

        if (!Directory.Exists(packDirectory))
            return;

        foreach (var indexPath in Directory.EnumerateFiles(packDirectory, "*.idx").Order(StringComparer.Ordinal))
        {
            var packPath = Path.ChangeExtension(indexPath, ".pack");

            if (!File.Exists(packPath))
            {
                throw new InvalidPackIndexException(
                    $"Pack index {indexPath} has no companion pack file at {packPath}.",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        indexPath: indexPath));
            }

            IReadOnlyList<PackIndex.IndexEntry> entries;
            byte[] indexData;

            try
            {
                indexData = File.ReadAllBytes(indexPath);
                entries = PackIndex.ParsePackIndexV2(indexData, indexPath);
            }
            catch (InvalidPackIndexException exception)
            {
                throw new InvalidPackIndexException(
                    exception.Message,
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        indexPath: indexPath,
                        indexVersion: exception.Context.IndexVersion,
                        observedValue: exception.Context.ObservedValue),
                    exception);
            }
            catch (Exception exception)
            {
                throw new InvalidPackIndexException(
                    $"Could not parse pack index {indexPath}.",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        indexPath: indexPath),
                    exception);
            }

            var packChecksum = indexData.AsMemory(indexData.Length - 40, 20);
            var pack = OpenPack(packPath, indexPath, entries, packChecksum);
            _packs.Add(pack);

            foreach (var entry in entries)
                _locations.TryAdd(entry.SHA1base16, new PackedObjectLocation(pack, entry));

            Report(
                new GitDiagnosticEvent(
                    GitDiagnosticEventKind.PackIndexed,
                    RepositoryPath,
                    "Open",
                    PackPath: packPath,
                    IndexPath: indexPath,
                    PackLength: pack.Length,
                    ByteCount: entries.Count));
        }
    }

    private PackInfo OpenPack(
        string packPath,
        string indexPath,
        IReadOnlyList<PackIndex.IndexEntry> entries,
        ReadOnlyMemory<byte> expectedPackChecksum)
    {
        FileStream? stream = null;

        try
        {
            stream =
                new FileStream(
                    packPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1,
                    FileOptions.RandomAccess);

            var packLength = stream.Length;

            if (packLength < 32)
            {
                throw new InvalidPackObjectException(
                    $"Pack file {packPath} is too short ({packLength} bytes).",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        packLength: packLength,
                        indexPath: indexPath,
                        observedValue: packLength));
            }

            Span<byte> header = stackalloc byte[12];
            ReadExactly(stream.SafeFileHandle, header, 0, packPath, null);

            if (!header[..4].SequenceEqual("PACK"u8))
            {
                throw new InvalidPackObjectException(
                    $"Pack file {packPath} has an invalid signature.",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        packLength: packLength,
                        indexPath: indexPath));
            }

            var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);

            if (version is not 2 and not 3)
            {
                throw new InvalidPackObjectException(
                    $"Pack file {packPath} uses unsupported version {version}.",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        packLength: packLength,
                        indexPath: indexPath,
                        observedValue: version));
            }

            var declaredObjectCount = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);

            if (declaredObjectCount != entries.Count)
            {
                throw new InvalidPackIndexException(
                    $"Index {indexPath} contains {entries.Count} entries, but pack {packPath} declares " +
                    $"{declaredObjectCount} objects.",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        packLength: packLength,
                        indexPath: indexPath,
                        indexVersion: 2,
                        observedValue: entries.Count));
            }

            var dataEnd = packLength - 20;
            Span<byte> actualPackChecksum = stackalloc byte[20];

            ReadExactly(
                stream.SafeFileHandle,
                actualPackChecksum,
                dataEnd,
                packPath,
                objectId: null);

            if (!actualPackChecksum.SequenceEqual(expectedPackChecksum.Span))
            {
                throw new InvalidPackIndexException(
                    $"Index {indexPath} references a different pack checksum than {packPath}.",
                    CreateErrorContext(
                        "Open",
                        packPath: packPath,
                        packLength: packLength,
                        indexPath: indexPath,
                        indexVersion: 2,
                        packOffset: dataEnd,
                        regionLength: 20));
            }

            var endsByOffset = new Dictionary<long, long>();
            var entriesByOffset = new Dictionary<long, PackIndex.IndexEntry>();

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var nextOffset = index + 1 < entries.Count ? entries[index + 1].Offset : dataEnd;

                if (entry.Offset < 12 || entry.Offset >= dataEnd || nextOffset <= entry.Offset)
                {
                    throw new InvalidPackIndexException(
                        $"Index {indexPath} contains invalid pack region [{entry.Offset}, {nextOffset}).",
                        CreateErrorContext(
                            "Open",
                            objectId: entry.SHA1base16,
                            packPath: packPath,
                            packLength: packLength,
                            indexPath: indexPath,
                            indexVersion: 2,
                            packOffset: entry.Offset,
                            regionLength: nextOffset - entry.Offset));
                }

                if (!entriesByOffset.TryAdd(entry.Offset, entry))
                {
                    throw new InvalidPackIndexException(
                        $"Index {indexPath} contains duplicate object offset {entry.Offset}.",
                        CreateErrorContext(
                            "Open",
                            objectId: entry.SHA1base16,
                            packPath: packPath,
                            packLength: packLength,
                            indexPath: indexPath,
                            packOffset: entry.Offset));
                }

                endsByOffset.Add(entry.Offset, nextOffset);
            }

            return
                new PackInfo(
                    packPath,
                    indexPath,
                    stream,
                    packLength,
                    entries.ToDictionary(entry => entry.SHA1base16, StringComparer.Ordinal),
                    entriesByOffset,
                    endsByOffset);
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    private GitObject? ResolveLocalObject(
        string objectId,
        ResolutionContext resolution,
        int deltaDepth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (resolution.ProvidedObjects.TryGetValue(objectId, out var provided))
            return provided;

        if (TryGetCached(objectId, out var cached))
            return cached;

        if (!resolution.Visited.Add($"object:{objectId}"))
        {
            throw new InvalidPackObjectException(
                $"Delta cycle detected while resolving object {objectId}.",
                CreateErrorContext(
                    "GetObject",
                    objectId: objectId,
                    observedValue: deltaDepth));
        }

        try
        {
            if (!_locations.TryGetValue(objectId, out var location))
                return null;

            GitObject gitObject =
                location switch
                {
                    LooseObjectLocation loose => ReadLooseObject(objectId, loose, cancellationToken),

                    PackedObjectLocation packed =>
                    ReadPackedObject(
                        packed.Pack,
                        packed.Entry,
                        resolution,
                        deltaDepth,
                        cancellationToken),

                    _ =>
                    throw new InvalidOperationException("Unsupported local object location.")
                };

            AddToCache(gitObject);
            return gitObject;
        }
        finally
        {
            resolution.Visited.Remove($"object:{objectId}");
        }
    }

    private GitObject ReadLooseObject(
        string objectId,
        LooseObjectLocation location,
        CancellationToken cancellationToken)
    {
        try
        {
            using var input =
                new FileStream(
                    location.Path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4_096,
                    FileOptions.SequentialScan);

            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var headerBytes = new List<byte>(64);

            while (headerBytes.Count < 1_024)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = zlib.ReadByte();

                if (value < 0)
                    throw new InvalidDataException("The loose-object header is truncated.");

                if (value is 0)
                    break;

                headerBytes.Add((byte)value);
            }

            if (headerBytes.Count >= 1_024)
                throw new InvalidDataException("The loose-object header exceeds 1,024 bytes.");

            var header = Encoding.ASCII.GetString([.. headerBytes]);
            var separator = header.IndexOf(' ');

            if (separator <= 0 ||
                !long.TryParse(
                    header.AsSpan(separator + 1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var declaredSize))
            {
                throw new InvalidDataException($"Invalid loose-object header '{header}'.");
            }

            var type =
                header[..separator] switch
                {
                    "commit" => PackFile.ObjectType.Commit,
                    "tree" => PackFile.ObjectType.Tree,
                    "blob" => PackFile.ObjectType.Blob,
                    "tag" => PackFile.ObjectType.Tag,

                    var typeName =>
                    throw new InvalidDataException($"Unsupported loose-object type '{typeName}'.")
                };

            EnsureMaterializable(
                declaredSize,
                "ReadLooseObject",
                objectId,
                objectType: type,
                expectedSize: declaredSize);

            var content = new byte[checked((int)declaredSize)];
            ReadExactly(zlib, content, cancellationToken);

            if (zlib.ReadByte() >= 0)
                throw new InvalidDataException("Loose object contains more content than its declared size.");

            var gitObject = new GitObject(type, declaredSize, content, objectId);
            ValidateObjectHash(gitObject);

            Report(
                new GitDiagnosticEvent(
                    GitDiagnosticEventKind.ObjectDecompressed,
                    RepositoryPath,
                    "ReadLooseObject",
                    objectId,
                    ObjectType: type,
                    ExpectedSize: declaredSize));

            return gitObject;
        }
        catch (GitRepositoryException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidPackObjectException(
                $"Loose object {objectId} at {location.Path} could not be read.",
                CreateErrorContext(
                    "ReadLooseObject",
                    objectId: objectId,
                    storagePath: location.Path),
                exception);
        }
    }

    private GitObject ReadPackedObject(
        PackInfo pack,
        PackIndex.IndexEntry entry,
        ResolutionContext resolution,
        int deltaDepth,
        CancellationToken cancellationToken)
    {
        var key = $"pack:{pack.Path}:{entry.Offset}";

        if (!resolution.Visited.Add(key))
        {
            throw new InvalidPackObjectException(
                $"Delta cycle detected at pack offset {entry.Offset} in {pack.Path}.",
                PackErrorContext(pack, entry, "ReadPackedObject"));
        }

        try
        {
            if (!pack.RegionEnds.TryGetValue(entry.Offset, out var regionEnd))
            {
                throw new InvalidPackObjectException(
                    $"No indexed region exists for offset {entry.Offset} in {pack.Path}.",
                    PackErrorContext(pack, entry, "ReadPackedObject"));
            }

            using var region =
                new RandomAccessRegionStream(
                    pack.Stream.SafeFileHandle,
                    entry.Offset,
                    regionEnd - entry.Offset,
                    bytes =>
                    Report(
                        new GitDiagnosticEvent(
                            GitDiagnosticEventKind.BytesRead,
                            RepositoryPath,
                            "ReadPackedObject",
                            entry.SHA1base16,
                            pack.Path,
                            pack.IndexPath,
                            pack.Length,
                            entry.Offset,
                            bytes)));

            var startOffset = entry.Offset;
            var first = ReadRequiredByte(region);
            var objectType = (PackFile.ObjectType)((first >> 4) & 0x07);
            long declaredSize = first & 0x0F;
            var shift = 4;
            var current = first;

            while ((current & 0x80) is not 0)
            {
                if (shift > 60)
                    throw new InvalidDataException("Packed object size encoding overflows Int64.");

                current = ReadRequiredByte(region);
                declaredSize |= (long)(current & 0x7F) << shift;
                shift += 7;
            }

            EnsureMaterializable(
                declaredSize,
                "ReadPackedObject",
                entry.SHA1base16,
                pack,
                entry.Offset,
                objectType,
                declaredSize);

            PackFile.ObjectType resolvedType;
            byte[] resolvedData;

            if (objectType is PackFile.ObjectType.OfsDelta)
            {
                EnsureDeltaDepth(deltaDepth + 1, pack, entry);
                long negativeOffset = ReadRequiredByte(region);
                current = checked((byte)negativeOffset);
                negativeOffset &= 0x7F;

                while ((current & 0x80) is not 0)
                {
                    current = ReadRequiredByte(region);
                    negativeOffset = checked(((negativeOffset + 1) << 7) | (long)(current & 0x7F));
                }

                var baseOffset = checked(startOffset - negativeOffset);

                if (baseOffset < 12 || !pack.EntriesByOffset.TryGetValue(baseOffset, out var baseEntry))
                {
                    throw new InvalidDataException(
                        $"OFS_DELTA at {startOffset} references non-indexed base offset {baseOffset}.");
                }

                var baseObject =
                    ReadPackedObject(
                        pack,
                        baseEntry,
                        resolution,
                        deltaDepth + 1,
                        cancellationToken);

                var deltaData = DecompressRegion(region, declaredSize, cancellationToken);
                resolvedData = ApplyDelta(baseObject.Data.Span, deltaData, pack, entry);
                resolvedType = baseObject.Type;
            }
            else if (objectType is PackFile.ObjectType.RefDelta)
            {
                EnsureDeltaDepth(deltaDepth + 1, pack, entry);
                Span<byte> baseIdBytes = stackalloc byte[20];
                ReadExactly(region, baseIdBytes, cancellationToken);
                var baseObjectId = Convert.ToHexStringLower(baseIdBytes);

                var baseObject =
                    ResolveLocalObject(
                        baseObjectId,
                        resolution,
                        deltaDepth + 1,
                        cancellationToken);

                if (baseObject is null)
                    throw new MissingDeltaBaseException(baseObjectId);

                var deltaData = DecompressRegion(region, declaredSize, cancellationToken);
                resolvedData = ApplyDelta(baseObject.Data.Span, deltaData, pack, entry);
                resolvedType = baseObject.Type;
            }
            else if (objectType is
                     PackFile.ObjectType.Commit or
                     PackFile.ObjectType.Tree or
                     PackFile.ObjectType.Blob or
                     PackFile.ObjectType.Tag)
            {
                resolvedData = DecompressRegion(region, declaredSize, cancellationToken);
                resolvedType = objectType;
            }
            else
            {
                throw new InvalidDataException($"Unsupported packed object type value {(int)objectType}.");
            }

            var gitObject =
                new GitObject(
                    resolvedType,
                    resolvedData.LongLength,
                    resolvedData,
                    entry.SHA1base16);

            ValidateObjectHash(gitObject);

            Report(
                new GitDiagnosticEvent(
                    GitDiagnosticEventKind.ObjectDecompressed,
                    RepositoryPath,
                    "ReadPackedObject",
                    entry.SHA1base16,
                    pack.Path,
                    pack.IndexPath,
                    pack.Length,
                    entry.Offset,
                    ObjectType: resolvedType,
                    ExpectedSize: resolvedData.LongLength));

            return gitObject;
        }
        catch (GitRepositoryException)
        {
            throw;
        }
        catch (MissingDeltaBaseException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidPackObjectException(
                $"Packed object {entry.SHA1base16} at offset {entry.Offset} in {pack.Path} could not be read.",
                PackErrorContext(pack, entry, "ReadPackedObject"),
                exception);
        }
        finally
        {
            resolution.Visited.Remove(key);
        }
    }

    private byte[] DecompressRegion(
        Stream compressedRegion,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var output = new byte[checked((int)expectedSize)];
        using var zlib = new ZLibStream(compressedRegion, CompressionMode.Decompress, leaveOpen: true);
        ReadExactly(zlib, output, cancellationToken);

        if (zlib.ReadByte() >= 0)
            throw new InvalidDataException($"Decompressed content exceeds declared size {expectedSize}.");

        return output;
    }

    private byte[] ApplyDelta(
        ReadOnlySpan<byte> baseData,
        ReadOnlySpan<byte> deltaData,
        PackInfo pack,
        PackIndex.IndexEntry entry)
    {
        try
        {
            var offset = 0;
            var declaredBaseSize = ReadDeltaSize(deltaData, ref offset);
            var resultSize = ReadDeltaSize(deltaData, ref offset);

            if (declaredBaseSize != baseData.Length)
            {
                throw new InvalidDataException(
                    $"Delta base size is {declaredBaseSize}, but the resolved base has {baseData.Length} bytes.");
            }

            EnsureMaterializable(
                resultSize,
                "ApplyDelta",
                entry.SHA1base16,
                pack,
                entry.Offset,
                expectedSize: resultSize);

            return PackFile.ApplyDelta(baseData, deltaData);
        }
        catch (GitRepositoryException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidPackObjectException(
                $"Delta object {entry.SHA1base16} at offset {entry.Offset} in {pack.Path} is invalid.",
                PackErrorContext(pack, entry, "ApplyDelta"),
                exception);
        }
    }

    private static long ReadDeltaSize(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong size = 0;
        var shift = 0;

        while (true)
        {
            if ((uint)offset >= (uint)data.Length || shift >= 63)
                throw new InvalidDataException("Delta size encoding is truncated or overflows Int64.");

            var value = data[offset++];
            size |= (ulong)(value & 0x7F) << shift;

            if ((value & 0x80) is 0)
                break;

            shift += 7;
        }

        if (size > long.MaxValue)
            throw new InvalidDataException("Delta size exceeds Int64.");

        return (long)size;
    }

    private void EnsureMaterializable(
        long size,
        string operation,
        string objectId,
        PackInfo? pack = null,
        long? packOffset = null,
        PackFile.ObjectType? objectType = null,
        long? expectedSize = null)
    {
        var runtimeLimit = Math.Min(_options.MaximumMaterializedObjectSize, int.MaxValue);

        if (size < 0 || size > runtimeLimit)
        {
            throw new GitObjectSizeLimitException(
                $"Object {objectId} requires {size} bytes, exceeding the materialization limit of {runtimeLimit}.",
                CreateErrorContext(
                    operation,
                    objectId: objectId,
                    packPath: pack?.Path,
                    packLength: pack?.Length,
                    indexPath: pack?.IndexPath,
                    packOffset: packOffset,
                    objectType: objectType,
                    expectedSize: expectedSize,
                    configuredLimit: runtimeLimit,
                    observedValue: size));
        }
    }

    private void EnsureDeltaDepth(
        int depth,
        PackInfo pack,
        PackIndex.IndexEntry entry)
    {
        if (depth <= _options.MaximumDeltaChainDepth)
            return;

        throw new GitResourceLimitException(
            $"Delta chain for {entry.SHA1base16} exceeds the configured depth of " +
            $"{_options.MaximumDeltaChainDepth}.",
            CreateErrorContext(
                "ReadPackedObject",
                objectId: entry.SHA1base16,
                packPath: pack.Path,
                packLength: pack.Length,
                indexPath: pack.IndexPath,
                packOffset: entry.Offset,
                configuredLimit: _options.MaximumDeltaChainDepth,
                observedValue: depth));
    }

    private bool TryGetCached(string objectId, out GitObject gitObject)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(objectId, out var node))
            {
                gitObject = null!;
                return false;
            }

            _cacheLru.Remove(node);
            _cacheLru.AddFirst(node);
            gitObject = node.Value.Object;
        }

        Report(
            new GitDiagnosticEvent(
                GitDiagnosticEventKind.CacheHit,
                RepositoryPath,
                "GetObject",
                objectId));

        return true;
    }

    private void AddToCache(GitObject gitObject)
    {
        if (gitObject.Type is PackFile.ObjectType.Blob && !_options.CacheBlobContents)
            return;

        if (_options.MaximumCachedObjectCount is 0 || _options.MaximumCachedObjectBytes is 0)
            return;

        if (_options.MaximumCachedObjectBytes is { } maximumBytes && gitObject.Size > maximumBytes)
            return;

        var evicted = new List<GitObject>();

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(gitObject.ObjectId, out var existing))
            {
                _cacheLru.Remove(existing);
                _cachedBytes -= existing.Value.Object.Size;
                _cache.Remove(gitObject.ObjectId);
            }

            var node = _cacheLru.AddFirst(new CacheEntry(gitObject));
            _cache.Add(gitObject.ObjectId, node);
            _cachedBytes += gitObject.Size;

            while ((_options.MaximumCachedObjectCount is { } maximumCount && _cache.Count > maximumCount) ||
                (_options.MaximumCachedObjectBytes is { } byteLimit && _cachedBytes > byteLimit))
            {
                var last = _cacheLru.Last!;
                _cacheLru.RemoveLast();
                _cache.Remove(last.Value.Object.ObjectId);
                _cachedBytes -= last.Value.Object.Size;
                evicted.Add(last.Value.Object);
            }
        }

        foreach (var item in evicted)
        {
            Report(
                new GitDiagnosticEvent(
                    GitDiagnosticEventKind.CacheEvicted,
                    RepositoryPath,
                    "Cache",
                    item.ObjectId,
                    ObjectType: item.Type,
                    ExpectedSize: item.Size));
        }
    }

    private IEnumerable<GitTreeFile> TraverseTree(
        string treeId,
        IReadOnlyList<string> prefix,
        int depth,
        string commitId,
        TreeTraversalOptions options,
        TraversalState state,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTreeDepth(depth, treeId, commitId, state.MaximumTreeDepth);
        var treeObject = GetRequiredObject(treeId, cancellationToken);

        if (treeObject.Type is not PackFile.ObjectType.Tree)
        {
            throw new GitRepositoryException(
                $"Object {treeId} is {treeObject.Type}, not a tree.",
                CreateErrorContext(
                    "EnumerateTree",
                    objectId: treeId,
                    commitId: commitId,
                    treeId: treeId,
                    objectType: treeObject.Type));
        }

        GitObjects.TreeObject tree;

        try
        {
            tree = GitObjects.ParseTree(treeObject.Data);
        }
        catch (Exception exception)
        {
            throw new GitRepositoryException(
                $"Tree {treeId} could not be parsed.",
                CreateErrorContext(
                    "EnumerateTree",
                    objectId: treeId,
                    commitId: commitId,
                    treeId: treeId,
                    objectType: treeObject.Type),
                exception);
        }

        foreach (var entry in tree.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = prefix.Concat([entry.Name]).ToArray();

            if (entry.Mode is "40000")
            {
                var selection =
                    options.Select(
                        new TreeTraversalSubtree(
                            path,
                            entry.Name,
                            entry.HashBase16));

                if (selection is TreeSubtreeSelection.Skip)
                {
                    ReportSkipped(GitDiagnosticEventKind.SkippedSubtree, entry, path);
                    continue;
                }

                if (selection is not TreeSubtreeSelection.Descend)
                {
                    throw new ArgumentException(
                        $"Subtree selector returned {selection} for tree {string.Join("/", path)}; " +
                        $"return Descend or Skip.");
                }

                foreach (var child in TraverseTree(
                    entry.HashBase16,
                    path,
                    depth + 1,
                    commitId,
                    options,
                    state,
                    cancellationToken))
                {
                    yield return child;
                }

                continue;
            }

            var isFileMode =
                entry.Mode.StartsWith("100", StringComparison.Ordinal) ||
                entry.Mode is "120000";

            if (!isFileMode)
            {
                ReportSkipped(GitDiagnosticEventKind.SkippedBlob, entry, path);
                continue;
            }

            var fileSelection =
                options.Select(
                    new TreeTraversalFile(
                        path,
                        entry.Name,
                        entry.HashBase16,
                        entry.Mode));

            if (fileSelection is TreeFileSelection.Skip)
            {
                ReportSkipped(GitDiagnosticEventKind.SkippedBlob, entry, path);
                continue;
            }

            if (fileSelection is not TreeFileSelection.Include)
            {
                throw new ArgumentException(
                    $"File selector returned {fileSelection} for file {string.Join("/", path)}; " +
                    $"return Include or Skip.");
            }

            IncrementSelectedEntryCount(state, entry.HashBase16, commitId, path);
            yield return CreateTreeFile(path, entry);
        }
    }

    private async IAsyncEnumerable<GitTreeFile> TraverseTreeAsync(
        string treeId,
        IReadOnlyList<string> prefix,
        int depth,
        string commitId,
        TreeTraversalOptions options,
        TraversalState state,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureTreeDepth(depth, treeId, commitId, state.MaximumTreeDepth);
        var treeObject = await GetRequiredObjectAsync(treeId, cancellationToken).ConfigureAwait(false);

        if (treeObject.Type is not PackFile.ObjectType.Tree)
        {
            throw new GitRepositoryException(
                $"Object {treeId} is {treeObject.Type}, not a tree.",
                CreateErrorContext(
                    "EnumerateTreeAsync",
                    objectId: treeId,
                    commitId: commitId,
                    treeId: treeId,
                    objectType: treeObject.Type));
        }

        GitObjects.TreeObject tree;

        try
        {
            tree = GitObjects.ParseTree(treeObject.Data);
        }
        catch (Exception exception)
        {
            throw new GitRepositoryException(
                $"Tree {treeId} could not be parsed.",
                CreateErrorContext(
                    "EnumerateTreeAsync",
                    objectId: treeId,
                    commitId: commitId,
                    treeId: treeId,
                    objectType: treeObject.Type),
                exception);
        }

        foreach (var entry in tree.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = prefix.Concat([entry.Name]).ToArray();

            if (entry.Mode is "40000")
            {
                var selection =
                    options.Select(
                        new TreeTraversalSubtree(
                            path,
                            entry.Name,
                            entry.HashBase16));

                if (selection is TreeSubtreeSelection.Skip)
                {
                    ReportSkipped(GitDiagnosticEventKind.SkippedSubtree, entry, path);
                    continue;
                }

                if (selection is not TreeSubtreeSelection.Descend)
                {
                    throw new ArgumentException(
                        $"Subtree selector returned {selection} for tree {string.Join("/", path)}; " +
                        $"return Descend or Skip.");
                }

                await foreach (var child in TraverseTreeAsync(
                    entry.HashBase16,
                    path,
                    depth + 1,
                    commitId,
                    options,
                    state,
                    cancellationToken))
                {
                    yield return child;
                }

                continue;
            }

            var isFileMode =
                entry.Mode.StartsWith("100", StringComparison.Ordinal) ||
                entry.Mode is "120000";

            if (!isFileMode)
            {
                ReportSkipped(GitDiagnosticEventKind.SkippedBlob, entry, path);
                continue;
            }

            var fileSelection =
                options.Select(
                    new TreeTraversalFile(
                        path,
                        entry.Name,
                        entry.HashBase16,
                        entry.Mode));

            if (fileSelection is TreeFileSelection.Skip)
            {
                ReportSkipped(GitDiagnosticEventKind.SkippedBlob, entry, path);
                continue;
            }

            if (fileSelection is not TreeFileSelection.Include)
            {
                throw new ArgumentException(
                    $"File selector returned {fileSelection} for file {string.Join("/", path)}; " +
                    $"return Include or Skip.");
            }

            IncrementSelectedEntryCount(state, entry.HashBase16, commitId, path);
            yield return CreateTreeFile(path, entry);
        }
    }

    private GitTreeFile CreateTreeFile(
        IReadOnlyList<string> path,
        GitObjects.TreeEntry entry)
    {
        return
            new GitTreeFile(
                path,
                entry.HashBase16,
                entry.Mode,
                size: null,
                async cancellationToken =>
                {
                    var gitObject =
                        await GetRequiredObjectAsync(entry.HashBase16, cancellationToken).ConfigureAwait(false);

                    if (gitObject.Type is not PackFile.ObjectType.Blob)
                    {
                        throw new GitRepositoryException(
                            $"Tree entry {string.Join("/", path)} references {gitObject.Type}, not a blob.",
                            CreateErrorContext(
                                "OpenContentAsync",
                                objectId: entry.HashBase16,
                                objectType: gitObject.Type));
                    }

                    return new MemoryStream(gitObject.Data.ToArray(), writable: false);
                },
                (destination, cancellationToken) =>
                CopyBlobToAsync(entry.HashBase16, destination, cancellationToken));
    }

    private TraversalState CreateTraversalState(TreeTraversalOptions options)
    {
        var maximumDepth = options.MaximumTreeDepth ?? _options.MaximumTreeDepth;
        var maximumEntries = options.MaximumSelectedEntryCount ?? _options.MaximumSelectedEntryCount;

        if (maximumDepth < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumTreeDepth));

        if (maximumEntries < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumSelectedEntryCount));

        return new TraversalState(maximumDepth, maximumEntries);
    }

    private void EnsureTreeDepth(
        int depth,
        string treeId,
        string commitId,
        int maximumDepth)
    {
        if (depth <= maximumDepth)
            return;

        throw new GitResourceLimitException(
            $"Tree {treeId} exceeds the configured traversal depth of {maximumDepth}.",
            CreateErrorContext(
                "EnumerateTree",
                objectId: treeId,
                commitId: commitId,
                treeId: treeId,
                configuredLimit: maximumDepth,
                observedValue: depth));
    }

    private void IncrementSelectedEntryCount(
        TraversalState state,
        string objectId,
        string commitId,
        IReadOnlyList<string> path)
    {
        state.SelectedEntryCount++;

        if (state.SelectedEntryCount <= state.MaximumSelectedEntryCount)
            return;

        throw new GitResourceLimitException(
            $"Tree traversal exceeded the configured selected-entry limit of " +
            $"{state.MaximumSelectedEntryCount} at {string.Join("/", path)}.",
            CreateErrorContext(
                "EnumerateTree",
                objectId: objectId,
                commitId: commitId,
                configuredLimit: state.MaximumSelectedEntryCount,
                observedValue: state.SelectedEntryCount));
    }

    private void ReportSkipped(
        GitDiagnosticEventKind kind,
        GitObjects.TreeEntry entry,
        IReadOnlyList<string> path)
    {
        Report(
            new GitDiagnosticEvent(
                kind,
                RepositoryPath,
                "EnumerateTree",
                entry.HashBase16,
                Path: path));
    }

    private static RepositoryLayout DiscoverLayout(string repositoryPath)
    {
        var originalPath = Path.GetFullPath(repositoryPath);
        string gitDirectory;

        if (File.Exists(originalPath))
        {
            gitDirectory = ReadGitDirectoryFile(originalPath);
        }
        else if (Directory.Exists(originalPath))
        {
            var dotGit = Path.Combine(originalPath, ".git");

            if (Directory.Exists(dotGit))
                gitDirectory = Path.GetFullPath(dotGit);

            else if (File.Exists(dotGit))
                gitDirectory = ReadGitDirectoryFile(dotGit);

            else if (Directory.Exists(Path.Combine(originalPath, "objects")) &&
                File.Exists(Path.Combine(originalPath, "HEAD")))
                gitDirectory = originalPath;

            else
            {
                throw new InvalidOperationException(
                    $"Path {originalPath} is not a worktree, Git directory, or bare repository.");
            }
        }
        else
        {
            throw new DirectoryNotFoundException($"Repository path not found: {originalPath}");
        }

        if (!Directory.Exists(gitDirectory))
            throw new DirectoryNotFoundException($"Git directory not found: {gitDirectory}");

        var commonDirectory = gitDirectory;
        var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");

        if (File.Exists(commonDirectoryFile))
        {
            var configured = File.ReadAllText(commonDirectoryFile).Trim();

            if (string.IsNullOrWhiteSpace(configured))
                throw new InvalidOperationException($"Git commondir file {commonDirectoryFile} is empty.");

            commonDirectory =
                Path.GetFullPath(
                    Path.IsPathRooted(configured)
                    ?
                    configured
                    :
                    Path.Combine(gitDirectory, configured));
        }

        var primaryObjects = Path.Combine(commonDirectory, "objects");

        if (!Directory.Exists(primaryObjects))
        {
            throw new InvalidOperationException(
                $"Not a valid Git repository (missing objects directory): {primaryObjects}");
        }

        var objectDirectories = DiscoverObjectDirectories(primaryObjects);

        return
            new RepositoryLayout(
                originalPath,
                Path.GetFullPath(gitDirectory),
                Path.GetFullPath(commonDirectory),
                objectDirectories);
    }

    private static string ReadGitDirectoryFile(string gitFilePath)
    {
        var content = File.ReadAllText(gitFilePath).Trim();
        const string Prefix = "gitdir:";

        if (!content.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Git file {gitFilePath} does not contain a gitdir directive.");

        var configuredPath = content[Prefix.Length..].Trim();

        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new InvalidOperationException($"Git file {gitFilePath} contains an empty gitdir directive.");

        return
            Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                ?
                configuredPath
                :
                Path.Combine(Path.GetDirectoryName(gitFilePath)!, configuredPath));
    }

    private static IReadOnlyList<string> DiscoverObjectDirectories(string primaryObjects)
    {
        var result = new List<string>();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(PathComparer);
        pending.Enqueue(Path.GetFullPath(primaryObjects));

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            if (!visited.Add(current))
                continue;

            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException($"Git object directory not found: {current}");

            result.Add(current);
            var alternatesPath = Path.Combine(current, "info", "alternates");

            if (!File.Exists(alternatesPath))
                continue;

            foreach (var rawLine in File.ReadLines(alternatesPath))
            {
                var line = rawLine.Trim();

                if (line.Length is 0)
                    continue;

                if (line.Length >= 2 && line[0] is '"' && line[^1] is '"')
                    line = line[1..^1];

                var alternate =
                    Path.GetFullPath(
                        Path.IsPathRooted(line)
                        ?
                        line
                        :
                        Path.Combine(current, line));

                pending.Enqueue(alternate);
            }
        }

        return result.AsReadOnly();
    }

    private static (IReadOnlyList<string> PromisorRemotes, IReadOnlyList<string> Filters)
        ReadPartialCloneConfiguration(RepositoryLayout layout)
    {
        var remotes = new HashSet<string>(StringComparer.Ordinal);
        var filters = new HashSet<string>(StringComparer.Ordinal);

        foreach (var configPath in new[]
        {
            Path.Combine(layout.CommonGitDirectory, "config"),
            Path.Combine(layout.GitDirectory, "config.worktree")
        }.Distinct(PathComparer))
        {
            if (!File.Exists(configPath))
                continue;

            string? section = null;

            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();

                if (line.Length is 0 || line[0] is '#' or ';')
                    continue;

                if (line[0] is '[' && line[^1] is ']')
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                var equals = line.IndexOf('=');

                if (equals < 0 || section is null)
                    continue;

                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim().Trim('"');

                if (section.Equals("extensions", StringComparison.OrdinalIgnoreCase) &&
                    key.Equals("partialClone", StringComparison.OrdinalIgnoreCase) &&
                    value.Length > 0)
                {
                    remotes.Add(value);
                    continue;
                }

                if (!TryParseRemoteSection(section, out var remoteName))
                    continue;

                if (key.Equals("promisor", StringComparison.OrdinalIgnoreCase) &&
                    IsTrue(value))
                {
                    remotes.Add(remoteName);
                }
                else if (key.Equals("partialclonefilter", StringComparison.OrdinalIgnoreCase) &&
                    value.Length > 0)
                {
                    filters.Add(value);
                }
            }
        }

        return
            (remotes.Order(StringComparer.Ordinal).ToArray(),
            filters.Order(StringComparer.Ordinal).ToArray());
    }

    private static string? ResolveReferenceFromLayout(
        RepositoryLayout layout,
        string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        if (TryNormalizeObjectId(reference, out var directObjectId))
            return directObjectId;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = reference.Replace('\\', '/');

        for (var depth = 0; depth < 64; depth++)
        {
            if (!visited.Add(current))
            {
                throw new GitRepositoryException(
                    $"Symbolic reference cycle detected while resolving {reference}.",
                    new GitErrorContext(
                        "ResolveReference",
                        layout.RepositoryPath,
                        ObjectDirectories: layout.ObjectDirectories));
            }

            var content = ReadLooseReference(layout, current);

            if (content is null)
                return ResolveFromPackedRefs(layout, current);

            if (content.StartsWith("ref: ", StringComparison.Ordinal))
            {
                current = content[5..].Trim();
                continue;
            }

            return TryNormalizeObjectId(content.Trim(), out var objectId) ? objectId : null;
        }

        throw new GitResourceLimitException(
            $"Symbolic reference {reference} exceeded the maximum resolution depth of 64.",
            new GitErrorContext(
                "ResolveReference",
                layout.RepositoryPath,
                ConfiguredLimit: 64,
                ObservedValue: 65,
                ObjectDirectories: layout.ObjectDirectories));
    }

    private static string? ReadLooseReference(
        RepositoryLayout layout,
        string reference)
    {
        foreach (var directory in new[] { layout.GitDirectory, layout.CommonGitDirectory }
            .Distinct(PathComparer))
        {
            var path = GetSafeReferencePath(directory, reference);

            if (File.Exists(path))
                return File.ReadAllText(path).Trim();
        }

        return null;
    }

    private static string? ResolveFromPackedRefs(
        RepositoryLayout layout,
        string reference)
    {
        foreach (var directory in new[] { layout.GitDirectory, layout.CommonGitDirectory }
            .Distinct(PathComparer))
        {
            var packedRefsPath = Path.Combine(directory, "packed-refs");

            if (!File.Exists(packedRefsPath))
                continue;

            foreach (var line in File.ReadLines(packedRefsPath))
            {
                if (line.Length is 0 || line[0] is '#' or '^')
                    continue;

                var separator = line.IndexOf(' ');

                if (separator <= 0 || !line[(separator + 1)..].Equals(reference, StringComparison.Ordinal))
                    continue;

                return TryNormalizeObjectId(line[..separator], out var objectId) ? objectId : null;
            }
        }

        return null;
    }

    private static string GetSafeReferencePath(string gitDirectory, string reference)
    {
        if (Path.IsPathRooted(reference))
            throw new ArgumentException("Git references must be relative.", nameof(reference));

        var normalized = reference.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(gitDirectory, normalized));
        var root = Path.GetFullPath(gitDirectory) + Path.DirectorySeparatorChar;

        if (!path.StartsWith(root, PathComparison))
            throw new ArgumentException($"Git reference escapes the Git directory: {reference}", nameof(reference));

        return path;
    }

    private void ValidateProvidedObject(string requestedObjectId, GitObject provided)
    {
        if (!requestedObjectId.Equals(provided.ObjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidPackObjectException(
                $"Missing-object provider returned {provided.ObjectId} for request {requestedObjectId}.",
                CreateErrorContext(
                    "MissingObjectProvider",
                    objectId: requestedObjectId,
                    objectType: provided.Type,
                    expectedSize: provided.Size));
        }

        if (provided.Size != provided.Data.Length)
        {
            throw new InvalidPackObjectException(
                $"Provided object {requestedObjectId} declares {provided.Size} bytes but contains " +
                $"{provided.Data.Length}.",
                CreateErrorContext(
                    "MissingObjectProvider",
                    objectId: requestedObjectId,
                    objectType: provided.Type,
                    expectedSize: provided.Size,
                    observedValue: provided.Data.Length));
        }

        EnsureMaterializable(
            provided.Size,
            "MissingObjectProvider",
            requestedObjectId,
            objectType: provided.Type,
            expectedSize: provided.Size);

        ValidateObjectHash(provided with { ObjectId = requestedObjectId });
    }

    private void ValidateObjectHash(GitObject gitObject)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var header = Encoding.ASCII.GetBytes($"{ObjectTypeName(gitObject.Type)} {gitObject.Data.Length}\0");
        hash.AppendData(header);
        hash.AppendData(gitObject.Data.Span);
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());

        if (!actual.Equals(gitObject.ObjectId, StringComparison.Ordinal))
        {
            throw new InvalidPackObjectException(
                $"Object hash mismatch: expected {gitObject.ObjectId}, calculated {actual}.",
                CreateErrorContext(
                    "ValidateObject",
                    objectId: gitObject.ObjectId,
                    objectType: gitObject.Type,
                    expectedSize: gitObject.Size));
        }
    }

    private static string ObjectTypeName(PackFile.ObjectType objectType)
    {
        return
            objectType switch
            {
                PackFile.ObjectType.Commit => "commit",
                PackFile.ObjectType.Tree => "tree",
                PackFile.ObjectType.Blob => "blob",
                PackFile.ObjectType.Tag => "tag",

                _ =>
                throw new ArgumentOutOfRangeException(nameof(objectType), objectType, "Not a resolved object type.")
            };
    }

    private GitErrorContext PackErrorContext(
        PackInfo pack,
        PackIndex.IndexEntry entry,
        string operation)
    {
        return
            CreateErrorContext(
                operation,
                objectId: entry.SHA1base16,
                packPath: pack.Path,
                packLength: pack.Length,
                indexPath: pack.IndexPath,
                indexVersion: 2,
                packOffset: entry.Offset,
                regionLength:
                pack.RegionEnds.TryGetValue(entry.Offset, out var end)
                ?
                end - entry.Offset
                :
                null);
    }

    private GitErrorContext CreateErrorContext(
        string operation,
        string? objectId = null,
        string? commitId = null,
        string? treeId = null,
        string? packPath = null,
        long? packLength = null,
        string? indexPath = null,
        uint? indexVersion = null,
        long? packOffset = null,
        long? regionLength = null,
        PackFile.ObjectType? objectType = null,
        long? expectedSize = null,
        bool? isPartialClone = null,
        long? configuredLimit = null,
        long? observedValue = null,
        string? storagePath = null)
    {
        return
            new GitErrorContext(
                operation,
                RepositoryPath,
                objectId,
                commitId,
                treeId,
                packPath,
                packLength,
                indexPath,
                indexVersion,
                packOffset,
                regionLength,
                objectType,
                expectedSize,
                isPartialClone ?? IsPartialClone,
                PromisorRemotes,
                configuredLimit,
                observedValue,
                storagePath,
                ObjectDirectories);
    }

    private static void ReadExactly(
        SafeFileHandle handle,
        Span<byte> destination,
        long fileOffset,
        string packPath,
        string? objectId)
    {
        var read = 0;

        while (read < destination.Length)
        {
            var count = RandomAccess.Read(handle, destination[read..], checked(fileOffset + read));

            if (count is 0)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of pack {packPath} while reading object {objectId ?? "(header)"} " +
                    $"at offset {fileOffset + read}.");
            }

            read += count;
        }
    }

    private static void ReadExactly(
        Stream stream,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < destination.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = stream.Read(destination[read..]);

            if (count is 0)
                throw new EndOfStreamException("Unexpected end of compressed Git object.");

            read += count;
        }
    }

    private static byte ReadRequiredByte(Stream stream)
    {
        var value = stream.ReadByte();

        if (value < 0)
            throw new EndOfStreamException("Unexpected end of packed object header.");

        return (byte)value;
    }

    private static string NormalizeObjectId(string objectId)
    {
        if (!TryNormalizeObjectId(objectId, out var normalized))
        {
            throw new ArgumentException(
                "GitCore currently supports full 40-character hexadecimal SHA-1 object identifiers.",
                nameof(objectId));
        }

        return normalized;
    }

    private static bool TryNormalizeObjectId(string objectId, out string normalized)
    {
        normalized = objectId.Trim().ToLowerInvariant();

        if (normalized.Length is not 40 || !normalized.All(IsLowerHex))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static bool IsLowerHex(char value)
    {
        return value is >= '0' and <= '9' or >= 'a' and <= 'f';
    }

    private static bool TryParseRemoteSection(string section, out string remoteName)
    {
        const string Prefix = "remote \"";

        if (section.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) &&
            section.EndsWith('"'))
        {
            remoteName = section[Prefix.Length..^1];
            return remoteName.Length > 0;
        }

        remoteName = string.Empty;
        return false;
    }

    private static bool IsTrue(string value)
    {
        return
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value is "1";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Report(GitDiagnosticEvent diagnosticEvent)
    {
        _options.Diagnostics?.Invoke(diagnosticEvent);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record RepositoryLayout(
        string RepositoryPath,
        string GitDirectory,
        string CommonGitDirectory,
        IReadOnlyList<string> ObjectDirectories);

    private abstract record ObjectLocation;

    private sealed record LooseObjectLocation(string Path) : ObjectLocation;

    private sealed record PackedObjectLocation(
        PackInfo Pack,
        PackIndex.IndexEntry Entry) : ObjectLocation;

    private sealed class PackInfo(
        string path,
        string indexPath,
        FileStream stream,
        long length,
        IReadOnlyDictionary<string, PackIndex.IndexEntry> entriesById,
        IReadOnlyDictionary<long, PackIndex.IndexEntry> entriesByOffset,
        IReadOnlyDictionary<long, long> regionEnds) : IDisposable
    {
        public string Path { get; } = path;

        public string IndexPath { get; } = indexPath;

        public FileStream Stream { get; } = stream;

        public long Length { get; } = length;

        public IReadOnlyDictionary<string, PackIndex.IndexEntry> EntriesById { get; } = entriesById;

        public IReadOnlyDictionary<long, PackIndex.IndexEntry> EntriesByOffset { get; } = entriesByOffset;

        public IReadOnlyDictionary<long, long> RegionEnds { get; } = regionEnds;

        public void Dispose()
        {
            Stream.Dispose();
        }
    }

    private sealed record CacheEntry(GitObject Object);

    private sealed class ResolutionContext
    {
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, GitObject> ProvidedObjects { get; } = new(StringComparer.Ordinal);
    }

    private sealed class MissingDeltaBaseException(string objectId) : Exception
    {
        public string ObjectId { get; } = objectId;
    }

    private sealed class TraversalState(
        int maximumTreeDepth,
        long maximumSelectedEntryCount)
    {
        public int MaximumTreeDepth { get; } = maximumTreeDepth;

        public long MaximumSelectedEntryCount { get; } = maximumSelectedEntryCount;

        public long SelectedEntryCount { get; set; }
    }

    private sealed class RandomAccessRegionStream(
        SafeFileHandle handle,
        long start,
        long length,
        Action<int> reportRead) : Stream
    {
        private readonly SafeFileHandle _handle = handle;

        private readonly long _start = start;

        private readonly long _length = length;

        private readonly Action<int> _reportRead = reportRead;

        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            var remaining = _length - _position;

            if (remaining <= 0 || buffer.Length is 0)
                return 0;

            var requested = (int)Math.Min(buffer.Length, remaining);
            var count = RandomAccess.Read(_handle, buffer[..requested], checked(_start + _position));
            _position += count;

            if (count > 0)
                _reportRead(count);

            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target =
                origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(_position + offset),
                    SeekOrigin.End => checked(_length + offset),

                    _ =>
                    throw new ArgumentOutOfRangeException(nameof(origin))
                };

            if (target < 0 || target > _length)
                throw new IOException($"Seek target {target} is outside region length {_length}.");

            _position = target;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
