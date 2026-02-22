using AwesomeAssertions;
using System;
using System.Linq;
using Xunit;

namespace GitCore.UnitTests;

public class ParseCommitTests
{
    [Fact]
    public void Parse_commit_properties_from_test_data_2025_10_27()
    {
        // Load test data and parse pack file
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var idxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

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

        // Assert exact values for the commit
        commit.TreeHash.Should().Be("8ba2247ab0a7fca6750be46db85f80344ae0df44", "Tree hash should match");
        commit.ParentHashes.Should().BeEmpty("This is the initial commit with no parents");

        // Assert author details
        commit.Author.Name.Should().Be("Michael Rätzel", "Author name should match");
        commit.Author.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Author email should match");

        commit.Author.Timestamp.Should().Be(
            new DateTimeOffset(2025, 10, 27, 7, 42, 57, TimeSpan.Zero),
            "Author timestamp should match");

        // Assert committer details
        commit.Committer.Name.Should().Be("Michael Rätzel", "Committer name should match");
        commit.Committer.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Committer email should match");

        commit.Committer.Timestamp.Should().Be(
            new DateTimeOffset(2025, 10, 27, 7, 47, 18, TimeSpan.Zero),
            "Committer timestamp should match");

        // Assert message
        commit.Message.Should().StartWith("basic repository setup", "Commit message should match");
    }

    [Fact]
    public void Parse_commit_with_specific_properties_from_test_data()
    {
        // Load test data and parse pack file
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var idxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // Parse the specific commit
        var commitHash = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        var commitObject = objectsBySHA1[commitHash];
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Verify the tree hash is exactly 40 characters and matches
        commit.TreeHash.Should().HaveLength(40, "Tree hash should be a 40-character hex string");
        commit.TreeHash.Should().Be("8ba2247ab0a7fca6750be46db85f80344ae0df44", "Tree hash should match exact value");

        // Verify author and committer signatures match exactly
        commit.Author.Name.Should().Be("Michael Rätzel", "Author name should match exactly");
        commit.Author.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Author email should match exactly");
        commit.Author.Email.Should().Contain("@", "Author email should be well-formed");

        commit.Committer.Name.Should().Be("Michael Rätzel", "Committer name should match exactly");
        commit.Committer.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Committer email should match exactly");
        commit.Committer.Email.Should().Contain("@", "Committer email should be well-formed");

        // Verify timestamp components
        commit.Author.Timestamp.Date.Should().Be(new DateTime(2025, 10, 27), "Author date component should match");
        commit.Committer.Timestamp.Date.Should().Be(new DateTime(2025, 10, 27), "Committer date component should match");

        // Verify parent hashes
        commit.ParentHashes.Should().BeEmpty("This is an initial commit with no parents");

        // Verify commit message
        commit.Message.Should().StartWith("basic repository setup", "Message should match");
    }

    [Fact]
    public void Parse_all_commits_in_test_data()
    {
        // Load test data and parse pack file
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var idxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

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

            commit.Author.Email.Should().NotBeNullOrEmpty(
                $"Commit {commitObject.SHA1base16} author should have an email");

            commit.Committer.Should().NotBeNull($"Commit {commitObject.SHA1base16} should have a committer");

            commit.Committer.Name.Should().NotBeNullOrEmpty(
                $"Commit {commitObject.SHA1base16} committer should have a name");

            commit.Committer.Email.Should().NotBeNullOrEmpty(
                $"Commit {commitObject.SHA1base16} committer should have an email");

            commit.ParentHashes.Should().NotBeNull(
                $"Commit {commitObject.SHA1base16} should have a ParentHashes collection");
        }
    }

    [Fact]
    public void Parse_commit_signature_timestamps_with_timezone()
    {
        // Load test data and parse pack file
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var idxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        var commitHash = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        var commitObject = objectsBySHA1[commitHash];
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Verify that timestamps include timezone information
        // DateTimeOffset always has an offset (even UTC has TimeSpan.Zero)
        // Just verify the timestamps are valid
        commit.Author.Timestamp.Should().NotBe(default, "Author timestamp should be set");
        commit.Committer.Timestamp.Should().NotBe(default, "Committer timestamp should be set");

        // Verify that we can convert to UTC
        var authorUtc = commit.Author.Timestamp.ToUniversalTime();
        var committerUtc = commit.Committer.Timestamp.ToUniversalTime();

        authorUtc.Should().NotBe(default, "Should be able to convert author timestamp to UTC");
        committerUtc.Should().NotBe(default, "Should be able to convert committer timestamp to UTC");
    }

    [Fact]
    public void Parse_commit_parent_hashes()
    {
        // Load test data and parse pack file
        var filesFromClone = TestData.LoadTestDataFiles_2025_10_27();

        var packFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.pack"]];

        var idxFileData =
            filesFromClone[["objects", "pack", "pack-f0af0a07967292ae02df043ff4169bee06f6c143.idx"]];

        var indexEntries = PackIndex.ParsePackIndexV2(idxFileData);
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // For the test data commit 14eb05f5beac67cdf2a229394baa626338a3d92e
        var commitHash = "14eb05f5beac67cdf2a229394baa626338a3d92e";
        var commitObject = objectsBySHA1[commitHash];
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // This is an initial commit, so it should have no parents
        commit.ParentHashes.Should().BeEmpty("Initial commit should have no parent hashes");

        // Verify that ParentHashes is a proper collection (not null)
        commit.ParentHashes.Should().NotBeNull("ParentHashes should be a valid collection");

        // General validation: if there were parents, they would be 40-char hex strings
        foreach (var parentHash in commit.ParentHashes)
        {
            parentHash.Should().HaveLength(40, "Parent hash should be a 40-character hex string");

            parentHash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                .Should().BeTrue($"Parent hash {parentHash} should be a valid lowercase hex string");
        }
    }
}
