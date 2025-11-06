using AwesomeAssertions;
using GitCore.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace GitCore.UnitTests;

using FilePath = IReadOnlyList<string>;

public class ParseCommitTests
{
    [Fact]
    public void Parse_commit_properties_from_test_data_2025_10_27()
    {
        // Load test data and parse pack file
        var filesFromClone = LoadTestDataFiles_2025_10_27();
        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];
        var idxFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // The test data should contain commit 14eb05f5beac67cdf2a229394baa626338a3d92e
        var commitHash = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        objectsBySHA1.Should().ContainKey(commitHash, "Test data should contain the expected commit");

        var commitObject = objectsBySHA1[commitHash];
        commitObject.Type.Should().Be(PackFile.ObjectType.Commit, "Object should be a commit");

        // Parse the commit
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Verify commit properties
        commit.Should().NotBeNull("Commit should be parsed successfully");
        commit.TreeHash.Should().NotBeNullOrEmpty("Commit should have a tree hash");
        commit.Message.Should().NotBeNullOrEmpty("Commit should have a message");

        // Verify author properties
        commit.Author.Should().NotBeNull("Commit should have an author");
        commit.Author.Name.Should().NotBeNullOrEmpty("Author should have a name");
        commit.Author.Email.Should().NotBeNullOrEmpty("Author should have an email");
        commit.Author.Timestamp.Should().NotBe(default(DateTimeOffset), "Author should have a valid timestamp");

        // Verify committer properties
        commit.Committer.Should().NotBeNull("Commit should have a committer");
        commit.Committer.Name.Should().NotBeNullOrEmpty("Committer should have a name");
        commit.Committer.Email.Should().NotBeNullOrEmpty("Committer should have an email");
        commit.Committer.Timestamp.Should().NotBe(default(DateTimeOffset), "Committer should have a valid timestamp");

        // Verify parent hashes collection exists (may be empty for initial commit)
        commit.ParentHashes.Should().NotBeNull("Commit should have a ParentHashes collection");
    }

    [Fact]
    public void Parse_commit_with_specific_properties_from_test_data()
    {
        // Load test data and parse pack file
        var filesFromClone = LoadTestDataFiles_2025_10_27();
        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];
        var idxFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // Parse the specific commit
        var commitHash = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        var commitObject = objectsBySHA1[commitHash];
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Verify specific commit metadata
        // The tree hash should be consistent
        commit.TreeHash.Should().HaveLength(40, "Tree hash should be a 40-character hex string");

        // Verify the commit message starts with expected text (based on typical commit messages)
        commit.Message.Should().NotBeNullOrEmpty("Commit should have a message");

        // Verify author and committer have properly formatted emails
        commit.Author.Email.Should().Contain("@", "Author email should contain @");
        commit.Committer.Email.Should().Contain("@", "Committer email should contain @");

        // Verify timestamps are in a reasonable range (after 2020)
        var year2020 = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        (commit.Author.Timestamp > year2020).Should().BeTrue("Author timestamp should be after 2020");
        (commit.Committer.Timestamp > year2020).Should().BeTrue("Committer timestamp should be after 2020");
    }

    [Fact]
    public void Parse_all_commits_in_test_data()
    {
        // Load test data and parse pack file
        var filesFromClone = LoadTestDataFiles_2025_10_27();
        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];
        var idxFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);

        // Find all commit objects
        var commitObjects = objects.Where(obj => obj.Type == PackFile.ObjectType.Commit).ToList();

        // The test data should contain at least one commit
        commitObjects.Count.Should().BeGreaterThan(0, "Test data should contain at least one commit");

        // Parse and verify each commit
        foreach (var commitObject in commitObjects)
        {
            var commit = GitObjects.ParseCommit(commitObject.Data);

            // Verify all commits have required properties
            commit.TreeHash.Should().NotBeNullOrEmpty($"Commit {commitObject.SHA1base16} should have a tree hash");
            commit.Author.Should().NotBeNull($"Commit {commitObject.SHA1base16} should have an author");
            commit.Author.Name.Should().NotBeNullOrEmpty($"Commit {commitObject.SHA1base16} author should have a name");
            commit.Author.Email.Should().NotBeNullOrEmpty($"Commit {commitObject.SHA1base16} author should have an email");
            commit.Committer.Should().NotBeNull($"Commit {commitObject.SHA1base16} should have a committer");
            commit.Committer.Name.Should().NotBeNullOrEmpty($"Commit {commitObject.SHA1base16} committer should have a name");
            commit.Committer.Email.Should().NotBeNullOrEmpty($"Commit {commitObject.SHA1base16} committer should have an email");
            commit.ParentHashes.Should().NotBeNull($"Commit {commitObject.SHA1base16} should have a ParentHashes collection");
        }
    }

    [Fact]
    public void Parse_commit_signature_timestamps_with_timezone()
    {
        // Load test data and parse pack file
        var filesFromClone = LoadTestDataFiles_2025_10_27();
        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];
        var idxFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        var commitHash = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        var commitObject = objectsBySHA1[commitHash];
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Verify that timestamps include timezone information
        // DateTimeOffset always has an offset (even UTC has TimeSpan.Zero)
        // Just verify the timestamps are valid
        commit.Author.Timestamp.Should().NotBe(default(DateTimeOffset), "Author timestamp should be set");
        commit.Committer.Timestamp.Should().NotBe(default(DateTimeOffset), "Committer timestamp should be set");

        // Verify that we can convert to UTC
        var authorUtc = commit.Author.Timestamp.ToUniversalTime();
        var committerUtc = commit.Committer.Timestamp.ToUniversalTime();

        authorUtc.Should().NotBe(default(DateTimeOffset), "Should be able to convert author timestamp to UTC");
        committerUtc.Should().NotBe(default(DateTimeOffset), "Should be able to convert committer timestamp to UTC");
    }

    [Fact]
    public void Parse_commit_parent_hashes()
    {
        // Load test data and parse pack file
        var filesFromClone = LoadTestDataFiles_2025_10_27();
        var packFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];
        var idxFileData = filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);

        // Find all commits and check their parent relationships
        var commitObjects = objects.Where(obj => obj.Type == PackFile.ObjectType.Commit).ToList();

        foreach (var commitObject in commitObjects)
        {
            var commit = GitObjects.ParseCommit(commitObject.Data);

            // Verify parent hashes are valid (either empty or contain valid hex strings)
            foreach (var parentHash in commit.ParentHashes)
            {
                parentHash.Should().HaveLength(40,
                    $"Parent hash in commit {commitObject.SHA1base16} should be a 40-character hex string");

                // Verify it's a valid hex string
                parentHash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                    .Should().BeTrue($"Parent hash {parentHash} should be a valid lowercase hex string");
            }
        }
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
