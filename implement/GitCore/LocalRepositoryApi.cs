using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitCore;

/// <summary>
/// Represents a resolved Git object. The content remains valid after the object database is disposed.
/// </summary>
public sealed record GitObject(
    PackFile.ObjectType Type,
    long Size,
    ReadOnlyMemory<byte> Data,
    string ObjectId)
{
    /// <summary>
    /// Gets the SHA-1 object identifier using the name employed by the older GitCore APIs.
    /// </summary>
    public string SHA1base16 => ObjectId;
}

/// <summary>
/// Provides reusable access to Git objects without exposing their physical storage.
/// Implementations must support concurrent object lookups.
/// </summary>
public interface IGitObjectDatabase : IDisposable
{
    /// <summary>
    /// Gets an object, or <see langword="null"/> when it is unavailable under the configured policy.
    /// Use <see cref="LookupObject"/> when the reason for a missing object is required.
    /// </summary>
    GitObject? GetObject(string objectId);

    /// <summary>
    /// Gets an object asynchronously, or <see langword="null"/> when it is unavailable.
    /// </summary>
    ValueTask<GitObject?> GetObjectAsync(
        string objectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up an object and returns explicit missing/promised-object information.
    /// </summary>
    GitObjectLookupResult LookupObject(string objectId);

    /// <summary>
    /// Looks up an object asynchronously and returns explicit missing/promised-object information.
    /// </summary>
    ValueTask<GitObjectLookupResult> LookupObjectAsync(
        string objectId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes the outcome of an object lookup.
/// </summary>
public enum GitObjectLookupStatus
{
    /// <summary>
    /// Indicates that the object was found.
    /// </summary>
    Found,

    /// <summary>
    /// Indicates that the object is missing.
    /// </summary>
    Missing,

    /// <summary>
    /// Indicates that an object promised by a remote is missing locally.
    /// </summary>
    MissingPromised
}

/// <summary>
/// Contains either a resolved object or complete information about why it is absent.
/// </summary>
public sealed record GitObjectLookupResult(
    string ObjectId,
    GitObjectLookupStatus Status,
    GitObject? Object,
    GitObjectNotFoundException? Error)
{
    /// <summary>
    /// Gets whether the lookup found the object.
    /// </summary>
    public bool IsSuccess => Status is GitObjectLookupStatus.Found;
}

/// <summary>
/// Controls how an absent local object may be obtained.
/// </summary>
public enum MissingObjectPolicy
{
    /// <summary>
    /// Never invokes a provider or performs network access.
    /// </summary>
    LocalOnly,

    /// <summary>
    /// Invokes the caller-supplied provider for promised objects.
    /// </summary>
    FetchMissing,

    /// <summary>
    /// Invokes the caller-supplied provider for any missing object.
    /// </summary>
    Custom
}

/// <summary>
/// Describes an object requested from a caller-supplied provider.
/// </summary>
public sealed record MissingGitObjectRequest(
    string RepositoryPath,
    string ObjectId,
    bool IsPromised,
    IReadOnlyList<string> PromisorRemotes,
    IReadOnlyList<string> PartialCloneFilters);

/// <summary>
/// Obtains an absent object without GitCore taking ownership of credentials, networking, or persistence.
/// </summary>
public delegate ValueTask<GitObject?> MissingGitObjectProvider(
    MissingGitObjectRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Controls local object loading, caching, resource limits, and missing-object handling.
/// </summary>
public sealed class LocalRepositoryOptions
{
    /// <summary>
    /// Gets the maximum number of objects retained in the cache.
    /// </summary>
    public int? MaximumCachedObjectCount { get; init; } = 4_096;

    /// <summary>
    /// Gets the maximum total size of objects retained in the cache.
    /// </summary>
    public long? MaximumCachedObjectBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Gets whether blob contents are retained in the object cache.
    /// </summary>
    public bool CacheBlobContents { get; init; }

    /// <summary>
    /// Gets the maximum size of an object that may be materialized.
    /// </summary>
    public long MaximumMaterializedObjectSize { get; init; } = int.MaxValue;

    /// <summary>
    /// Gets the maximum number of deltas resolved in one chain.
    /// </summary>
    public int MaximumDeltaChainDepth { get; init; } = 128;

    /// <summary>
    /// Gets the maximum depth traversed in a tree.
    /// </summary>
    public int MaximumTreeDepth { get; init; } = 1_024;

    /// <summary>
    /// Gets the maximum number of entries selected during tree traversal.
    /// </summary>
    public long MaximumSelectedEntryCount { get; init; } = long.MaxValue;

    /// <summary>
    /// Gets the policy for handling objects absent from local storage.
    /// </summary>
    public MissingObjectPolicy MissingObjectPolicy { get; init; } = MissingObjectPolicy.LocalOnly;

    /// <summary>
    /// Gets the caller-supplied provider for missing objects.
    /// </summary>
    public MissingGitObjectProvider? MissingObjectProvider { get; init; }

    /// <summary>
    /// Gets the callback that receives diagnostic events.
    /// </summary>
    public Action<GitDiagnosticEvent>? Diagnostics { get; init; }
}

/// <summary>
/// Identifies an observable object-database operation.
/// </summary>
public enum GitDiagnosticEventKind
{
    /// <summary>
    /// Indicates that a repository was opened.
    /// </summary>
    RepositoryOpened,

    /// <summary>
    /// Indicates that a pack index was loaded.
    /// </summary>
    PackIndexed,

    /// <summary>
    /// Indicates that bytes were read from storage.
    /// </summary>
    BytesRead,

    /// <summary>
    /// Indicates that an object was decompressed.
    /// </summary>
    ObjectDecompressed,

    /// <summary>
    /// Indicates that an object was found in the cache.
    /// </summary>
    CacheHit,

    /// <summary>
    /// Indicates that an object was evicted from the cache.
    /// </summary>
    CacheEvicted,

    /// <summary>
    /// Indicates that an object was missing.
    /// </summary>
    MissingObject,

    /// <summary>
    /// Indicates that a missing object was requested from a provider.
    /// </summary>
    MissingObjectRequested,

    /// <summary>
    /// Indicates that tree traversal skipped a blob.
    /// </summary>
    SkippedBlob,

    /// <summary>
    /// Indicates that tree traversal skipped a subtree.
    /// </summary>
    SkippedSubtree
}

/// <summary>
/// Contains structured context for an object-database diagnostic event.
/// </summary>
public sealed record GitDiagnosticEvent(
    GitDiagnosticEventKind Kind,
    string RepositoryPath,
    string Operation,
    string? ObjectId = null,
    string? PackPath = null,
    string? IndexPath = null,
    long? PackLength = null,
    long? PackOffset = null,
    long? ByteCount = null,
    PackFile.ObjectType? ObjectType = null,
    long? ExpectedSize = null,
    IReadOnlyList<string>? Path = null);

/// <summary>
/// Carries storage and Git identity context alongside a typed failure.
/// </summary>
public sealed record GitErrorContext(
    string Operation,
    string? RepositoryPath = null,
    string? ObjectId = null,
    string? CommitId = null,
    string? TreeId = null,
    string? PackPath = null,
    long? PackLength = null,
    string? IndexPath = null,
    uint? IndexVersion = null,
    long? PackOffset = null,
    long? RegionLength = null,
    PackFile.ObjectType? ObjectType = null,
    long? ExpectedSize = null,
    bool IsPartialClone = false,
    IReadOnlyList<string>? PromisorRemotes = null,
    long? ConfiguredLimit = null,
    long? ObservedValue = null,
    string? StoragePath = null,
    IReadOnlyList<string>? ObjectDirectories = null);

/// <summary>
/// Exposes structured context for a Git operation failure.
/// </summary>
public interface IGitContextException
{
    /// <summary>
    /// Gets the context associated with the failure.
    /// </summary>
    GitErrorContext Context { get; }
}

/// <summary>
/// Base class for typed local repository failures.
/// </summary>
/// <remarks>
/// Initializes a repository exception with structured failure context.
/// </remarks>
public class GitRepositoryException(
    string message,
    GitErrorContext context,
    Exception? innerException = null) : InvalidOperationException(message, innerException), IGitContextException
{

    /// <summary>
    /// Gets the context associated with the failure.
    /// </summary>
    public GitErrorContext Context { get; } = context;
}

/// <summary>
/// Indicates that an object is unavailable in all configured local stores and providers.
/// </summary>
/// <remarks>
/// Initializes an exception for an unavailable Git object.
/// </remarks>
public sealed class GitObjectNotFoundException(
    string objectId,
    GitErrorContext context,
    Exception? innerException = null) : GitRepositoryException(
        context.IsPartialClone
                ? $"Git object {objectId} is missing locally and may be promised by remote " +
                  $"{string.Join(", ", context.PromisorRemotes ?? [])}."
                : $"Git object {objectId} was not found in the configured object stores.",
        context,
        innerException)
{

    /// <summary>
    /// Gets the identifier of the unavailable object.
    /// </summary>
    public string ObjectId { get; } = objectId;

    /// <summary>
    /// Gets whether a configured remote promised the unavailable object.
    /// </summary>
    public bool IsPromised { get; } = context.IsPartialClone;

    /// <summary>
    /// Gets the remotes that may provide the unavailable object.
    /// </summary>
    public IReadOnlyList<string> PromisorRemotes { get; } = context.PromisorRemotes ?? [];
}

/// <summary>
/// Indicates malformed or unsupported pack index data.
/// </summary>
/// <remarks>
/// Initializes an exception for invalid pack index data.
/// </remarks>
public sealed class InvalidPackIndexException(
    string message,
    GitErrorContext context,
    Exception? innerException = null) : ArgumentException(message, innerException), IGitContextException
{

    /// <summary>
    /// Gets the context associated with the failure.
    /// </summary>
    public GitErrorContext Context { get; } = context;
}

/// <summary>
/// Indicates malformed, truncated, or inconsistent packed object data.
/// </summary>
/// <remarks>
/// Initializes an exception for invalid packed object data.
/// </remarks>
public sealed class InvalidPackObjectException(
    string message,
    GitErrorContext context,
    Exception? innerException = null) : GitRepositoryException(message, context, innerException)
{
}

/// <summary>
/// Indicates that materializing an object would exceed a configured or runtime limit.
/// </summary>
/// <remarks>
/// Initializes an exception for an object that exceeds a size limit.
/// </remarks>
public sealed class GitObjectSizeLimitException(
    string message,
    GitErrorContext context,
    Exception? innerException = null) : GitRepositoryException(message, context, innerException)
{
}

/// <summary>
/// Indicates that a delta, tree, cache, or entry-count resource limit was exceeded.
/// </summary>
/// <remarks>
/// Initializes an exception for a repository resource limit violation.
/// </remarks>
public sealed class GitResourceLimitException(
    string message,
    GitErrorContext context,
    Exception? innerException = null) : GitRepositoryException(message, context, innerException)
{
}

/// <summary>
/// Selects whether traversal includes a file before loading its referenced blob.
/// </summary>
public enum TreeFileSelection
{
    /// <summary>
    /// Includes the file in the traversal result.
    /// </summary>
    Include,

    /// <summary>
    /// Skips the file.
    /// </summary>
    Skip
}

/// <summary>
/// Selects whether traversal descends into a subtree.
/// </summary>
public enum TreeSubtreeSelection
{
    /// <summary>
    /// Descends into the subtree.
    /// </summary>
    Descend,

    /// <summary>
    /// Skips the subtree and all its entries.
    /// </summary>
    Skip
}

/// <summary>
/// Describes a file entry presented to a traversal selector.
/// </summary>
public sealed record TreeTraversalFile(
    IReadOnlyList<string> Path,
    string Name,
    string ObjectId,
    string Mode);

/// <summary>
/// Describes a subtree entry presented to a traversal selector.
/// </summary>
public sealed record TreeTraversalSubtree(
    IReadOnlyList<string> Path,
    string Name,
    string ObjectId);

/// <summary>
/// Selects whether traversal includes a file before loading its referenced blob.
/// </summary>
public delegate TreeFileSelection TreeFileSelector(TreeTraversalFile file);

/// <summary>
/// Selects whether traversal descends into a subtree.
/// </summary>
public delegate TreeSubtreeSelection TreeSubtreeSelector(TreeTraversalSubtree subtree);

/// <summary>
/// Controls incremental tree traversal and early subtree/blob pruning.
/// </summary>
public sealed class TreeTraversalOptions
{
    /// <summary>
    /// Gets the selector applied to each file before its referenced blob is loaded.
    /// </summary>
    public TreeFileSelector? SelectFile { get; init; }

    /// <summary>
    /// Gets the selector applied to each subtree before its referenced tree is loaded.
    /// </summary>
    public TreeSubtreeSelector? SelectSubtree { get; init; }

    /// <summary>
    /// Creates a file selector from a path-only predicate.
    /// </summary>
    public static TreeFileSelector CreateFileSelector(
        Func<IReadOnlyList<string>, bool> shouldIncludeFile)
    {
        ArgumentNullException.ThrowIfNull(shouldIncludeFile);

        return
            file =>
                shouldIncludeFile(file.Path)
                ?
                TreeFileSelection.Include
                :
                TreeFileSelection.Skip;
    }

    /// <summary>
    /// Creates a subtree selector from a path-only predicate.
    /// </summary>
    public static TreeSubtreeSelector CreateSubtreeSelector(
        Func<IReadOnlyList<string>, bool> shouldDescend)
    {
        ArgumentNullException.ThrowIfNull(shouldDescend);

        return
            subtree =>
                shouldDescend(subtree.Path)
                ?
                TreeSubtreeSelection.Descend
                :
                TreeSubtreeSelection.Skip;
    }

    /// <summary>
    /// Creates traversal options from path-only predicates.
    /// </summary>
    public static TreeTraversalOptions FromPathPredicates(
        Func<IReadOnlyList<string>, bool>? shouldIncludeFile = null,
        Func<IReadOnlyList<string>, bool>? shouldDescend = null)
    {
        return
            new TreeTraversalOptions
            {
                SelectFile =
                    shouldIncludeFile is null
                    ?
                    null
                    :
                    CreateFileSelector(shouldIncludeFile),
                SelectSubtree =
                    shouldDescend is null
                    ?
                    null
                    :
                    CreateSubtreeSelector(shouldDescend)
            };
    }

    /// <summary>
    /// Gets the maximum tree depth for this traversal.
    /// </summary>
    public int? MaximumTreeDepth { get; init; }

    /// <summary>
    /// Gets the maximum number of entries selected by this traversal.
    /// </summary>
    public long? MaximumSelectedEntryCount { get; init; }

    internal TreeFileSelection Select(TreeTraversalFile file)
    {
        return SelectFile?.Invoke(file) ?? TreeFileSelection.Include;
    }

    internal TreeSubtreeSelection Select(TreeTraversalSubtree subtree)
    {
        return SelectSubtree?.Invoke(subtree) ?? TreeSubtreeSelection.Descend;
    }
}

/// <summary>
/// Describes a selected tree file without eagerly loading its blob content.
/// </summary>
public sealed class GitTreeFile
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _openContentAsync;

    private readonly Func<Stream, CancellationToken, ValueTask> _copyContentToAsync;

    internal GitTreeFile(
        IReadOnlyList<string> path,
        string objectId,
        string mode,
        long? size,
        Func<CancellationToken, ValueTask<Stream>> openContentAsync,
        Func<Stream, CancellationToken, ValueTask> copyContentToAsync)
    {
        Path = path;
        ObjectId = objectId;
        Mode = mode;
        Size = size;
        _openContentAsync = openContentAsync;
        _copyContentToAsync = copyContentToAsync;
    }

    /// <summary>
    /// Gets the path components from the traversal root to the file.
    /// </summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>
    /// Gets the file blob's object identifier.
    /// </summary>
    public string ObjectId { get; }

    /// <summary>
    /// Gets the Git tree entry mode.
    /// </summary>
    public string Mode { get; }

    /// <summary>
    /// Gets the blob size when it is available without loading the content.
    /// </summary>
    public long? Size { get; }

    /// <summary>
    /// Opens a readable stream containing the blob content.
    /// </summary>
    public Stream OpenContent()
    {
        return OpenContentAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Asynchronously opens a readable stream containing the blob content.
    /// </summary>
    public ValueTask<Stream> OpenContentAsync(CancellationToken cancellationToken = default)
    {
        return _openContentAsync(cancellationToken);
    }

    /// <summary>
    /// Asynchronously copies the blob content to a caller-owned stream.
    /// </summary>
    public ValueTask CopyContentToAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return _copyContentToAsync(destination, cancellationToken);
    }
}
