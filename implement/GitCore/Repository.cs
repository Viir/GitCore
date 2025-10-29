using System.Collections.Generic;

namespace GitCore;

/// <summary>
/// Represents an immutable Git repository containing fetched objects (commits, trees, and blobs).
/// </summary>
public record Repository(
    IReadOnlyDictionary<string, PackFile.PackObject> Objects)
{
    /// <summary>
    /// Creates an empty repository.
    /// </summary>
    public static Repository Empty { get; } = new Repository(
        new Dictionary<string, PackFile.PackObject>());

    /// <summary>
    /// Creates a new repository with additional objects merged in.
    /// </summary>
    /// <param name="additionalObjects">Additional objects to add to the repository</param>
    /// <returns>A new repository containing all objects from this repository plus the additional objects</returns>
    public Repository WithObjects(IReadOnlyDictionary<string, PackFile.PackObject> additionalObjects)
    {
        var merged = new Dictionary<string, PackFile.PackObject>(Objects);

        foreach (var (sha, obj) in additionalObjects)
        {
            merged[sha] = obj;
        }

        return new Repository(merged);
    }

    /// <summary>
    /// Gets an object by its SHA, or null if not found.
    /// </summary>
    public PackFile.PackObject? GetObject(string sha)
    {
        return Objects.TryGetValue(sha, out var obj) ? obj : null;
    }

    /// <summary>
    /// Checks if the repository contains an object with the given SHA.
    /// </summary>
    public bool ContainsObject(string sha)
    {
        return Objects.ContainsKey(sha);
    }
}
