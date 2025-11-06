using AwesomeAssertions;
using System;
using Xunit;

namespace GitCore.UnitTests;

public class ParsePackFileTests
{
    [Fact]
    public void Parse_pack_files_2025_10_27()
    {
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        filesFromClone.Should().ContainKey(["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]);

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

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
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var idxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

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
        var sha256Hex = Convert.ToHexStringLower(sha256);

        sha256Hex.Should().Be("3ac5bef607354b0b2b30ad140d34a4f393d12bfd375f9a8b881bb2b361cb21c7",
            "README.md content should match expected SHA256 hash");
    }

    [Fact]
    public void Generate_idx_and_rev_files_from_pack_file()
    {
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var expectedIdxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var expectedRevFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.rev"]];

        // Generate idx and rev files from pack file
        var result = PackIndex.GeneratePackIndexV2(packFileData);

        // Verify the generated idx file matches the expected one
        result.IndexData.Length.Should().Be(expectedIdxFileData.Length, "Generated .idx file should have the same size");
        result.IndexData.Span.SequenceEqual(expectedIdxFileData.Span).Should().BeTrue("Generated .idx file should match expected content");

        // Verify the generated rev file matches the expected one
        result.ReverseIndexData.Length.Should().Be(expectedRevFileData.Length, "Generated .rev file should have the same size");
        result.ReverseIndexData.Span.SequenceEqual(expectedRevFileData.Span).Should().BeTrue("Generated .rev file should match expected content");
    }
}
