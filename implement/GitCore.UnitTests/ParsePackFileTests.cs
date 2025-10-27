using AwesomeAssertions;
using GitCore.Common;
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace GitCore.UnitTests;

using FilePath = IReadOnlyList<string>;

public class ParsePackFileTests
{
    [Fact]
    public void Parse_pack_files_2025_10_27()
    {
        var filesFromClone = LoadTestDataFiles_2025_10_27();

        filesFromClone.Should().ContainKey(["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]);

        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        // Parse pack file header
        var header = PackFile.ParsePackFileHeader(packFileData);

        // Verify pack file version
        header.Version.Should().Be(2u, "Pack file should use version 2 format");

        // Verify number of objects in pack file
        header.ObjectCount.Should().Be(6u, "Pack file should contain 6 objects");

        // Verify pack file checksum
        PackFile.VerifyPackFileChecksum(packFileData).Should().BeTrue("Pack file checksum should be valid");
    }

    [Fact]
    public void Parse_pack_file_objects_and_get_file_from_commit()
    {
        var filesFromClone = LoadTestDataFiles_2025_10_27();
        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];
        var idxFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        // Parse the index file
        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        indexEntries.Count.Should().Be(6, "Index should contain 6 entries");

        // Parse all objects from pack file using the index
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        
        // Verify we got all 6 objects
        objects.Count.Should().Be(6, "Pack file should contain 6 objects");

        // Create a dictionary for easy lookup
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // Get README.md from the commit
        var commitSHA1 = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        var readmeContent = GitObjects.GetFileFromCommit(commitSHA1, "README.md", objectsBySHA1);

        // Verify the SHA256 hash of the README.md content
        var sha256 = System.Security.Cryptography.SHA256.HashData(readmeContent.Span);
        var sha256Hex = System.Convert.ToHexStringLower(sha256);

        sha256Hex.Should().Be("3ac5bef607354b0b2b30ad140d34a4f393d12bfd375f9a8b881bb2b361cb21c7",
            "README.md content should match expected SHA256 hash");
    }

    private static IReadOnlyDictionary<FilePath, ReadOnlyMemory<byte>> LoadTestDataFiles_2025_10_27()
    {
        var testDataDir =
            Path.Combine(
                AppContext.BaseDirectory,
                "TestData",
                "2025-10-27-clone",
                "files-after-git-clone");

        Directory.Exists(testDataDir).Should().BeTrue($"Missing test data at: {testDataDir}");

        var result =
            new Dictionary<FilePath, ReadOnlyMemory<byte>>(
                comparer: EnumerableExtensions.EqualityComparer<FilePath>());

        foreach (var file in Directory.EnumerateFiles(testDataDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(testDataDir, file);

            var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var bytes = File.ReadAllBytes(file);

            result.Add(pathParts, bytes);
        }

        return result;
    }
}
