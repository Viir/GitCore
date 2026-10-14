using System;
using System.IO;

namespace GitCore;

/// <summary>
/// Describes a filesystem entry without following symbolic links.
/// </summary>
public enum GitFileSystemEntryKind
{
    /// <summary>No entry exists at the path.</summary>
    Missing,

    /// <summary>A regular file.</summary>
    File,

    /// <summary>A directory.</summary>
    Directory,

    /// <summary>A symbolic link or reparse point.</summary>
    SymbolicLink
}

/// <summary>
/// Supplies all filesystem and environment access needed by <see cref="GitWorkingTree"/>.
/// Paths passed to implementations are absolute native paths. Errors other than missing entries
/// must propagate rather than being treated as empty files or absent repositories.
/// </summary>
public interface IGitFileSystem
{
    /// <summary>Inspects an entry without following symbolic links.</summary>
    GitFileSystemEntryKind GetEntryKind(string absolutePath);

    /// <summary>
    /// Reads a file, returning null only when it does not exist.
    /// </summary>
    byte[]? ReadAllBytes(string absolutePath);

    /// <summary>Gets an environment variable, or null when it is unset.</summary>
    string? GetEnvironmentVariable(string name);
}

/// <summary>
/// Implements <see cref="IGitFileSystem"/> using the operating system.
/// </summary>
public sealed class PhysicalGitFileSystem : IGitFileSystem
{
    /// <summary>Gets the shared stateless filesystem implementation.</summary>
    public static PhysicalGitFileSystem Instance { get; } = new();

    /// <inheritdoc/>
    public GitFileSystemEntryKind GetEntryKind(string absolutePath)
    {
        try
        {
            var attributes = File.GetAttributes(absolutePath);

            if ((attributes & FileAttributes.ReparsePoint) is not 0)
                return GitFileSystemEntryKind.SymbolicLink;

            return
                (attributes & FileAttributes.Directory) is not 0
                ?
                GitFileSystemEntryKind.Directory
                :
                GitFileSystemEntryKind.File;
        }
        catch (FileNotFoundException)
        {
            return GitFileSystemEntryKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return GitFileSystemEntryKind.Missing;
        }
    }

    /// <inheritdoc/>
    public byte[]? ReadAllBytes(string absolutePath)
    {
        try
        {
            return File.ReadAllBytes(absolutePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }
}
