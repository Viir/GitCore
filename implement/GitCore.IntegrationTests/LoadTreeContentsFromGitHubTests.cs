using AwesomeAssertions;
using System;
using System.Linq;
using Xunit;

namespace GitCore.IntegrationTests;

public class LoadFromGitHubTests
{
    [Fact]
    public void Load_tree_at_root_via_commit()
    {
        var treeContents =
            LoadFromUrl.LoadTreeContentsFromUrl(
                "https://github.com/Viir/GitCore/tree/14eb05f5beac67cdf2a229394baa626338a3d92e");

        var readmeFile = treeContents[["README.md"]];

        var readmeSHA256 = System.Security.Cryptography.SHA256.HashData(readmeFile.Span);

        var readmeSHA256Hex = System.Convert.ToHexStringLower(readmeSHA256);

        readmeSHA256Hex.Should().Be("3ac5bef607354b0b2b30ad140d34a4f393d12bfd375f9a8b881bb2b361cb21c7");
    }

    [Fact]
    public void Load_tree_at_root_via_named_branch()
    {
        var treeContents =
            LoadFromUrl.LoadTreeContentsFromUrl(
                "https://github.com/Viir/GitCore/tree/main");

        // Assert that README.md exists at the root
        var readmeFile = treeContents[["README.md"]];
        readmeFile.Length.Should().BeGreaterThan(0, "README.md should exist and have content");

        // Assert that there's at least one file in the "implement" subdirectory
        var hasImplementSubdir = treeContents.Keys
            .Any(path => path.Count >= 2 && path[0] is "implement");

        hasImplementSubdir.Should().BeTrue("There should be files in the 'implement' subdirectory");
    }

    [Fact]
    public void Load_tree_from_repository_url_and_commit_sha()
    {
        // Test inputs: repository URL and commit SHA
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "c3135b803587ce0b4bf8f04f089f58ca4f27015c";

        // Fetch the pack file directly using the git URL
        var packFileData =
            GitSmartHttp.FetchPackFileAsync(repositoryUrl, commitSha)
            .GetAwaiter()
            .GetResult();

        // Generate index for the pack file
        var indexResult = PackIndex.GeneratePackIndexV2(packFileData);
        var indexEntries = PackIndex.ParsePackIndexV2(indexResult.IndexData);

        // Parse all objects from the pack file
        var objects = PackFile.ParseAllObjects(packFileData, indexEntries);
        var objectsBySHA1 = PackFile.GetObjectsBySHA1(objects);

        // Get the commit object
        if (!objectsBySHA1.TryGetValue(commitSha, out var commitObject))
        {
            throw new InvalidOperationException($"Commit {commitSha} not found in pack file");
        }

        if (commitObject.Type is not PackFile.ObjectType.Commit)
        {
            throw new InvalidOperationException($"Object {commitSha} is not a commit");
        }

        // Parse the commit to get the tree SHA
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Get all files from the tree recursively
        var treeContents = GitObjects.GetAllFilesFromTree(
            commit.TreeSHA1,
            sha => objectsBySHA1.TryGetValue(sha, out var obj) ? obj : null);

        // Verify that the tree was loaded successfully
        treeContents.Should().NotBeNull("Tree should be loaded");
        treeContents.Count.Should().BeGreaterThan(0, "Tree should contain files");

        // Verify that README.md exists using the same pattern as other tests
        var readmeFile = treeContents[["README.md"]];
        readmeFile.Length.Should().BeGreaterThan(0, "README.md should exist and have content");
    }

    [Fact]
    public void Placeholder()
    {
        /*
         * Avoid "Zero tests ran" error in CI as long as there are no real tests yet.
         * */
    }
}
