using AwesomeAssertions;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GitCore.IntegrationTests;

public class BloblessCloneApiTests
{
    [Fact]
    public async Task FetchBloblessClone_returns_commits_and_trees_without_blobs()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";

        // Act
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        // Assert
        result.Should().NotBeNull("Result should not be null");
        result.CommitSha.Should().Be(commitSha, "Commit SHA should match the requested commit");
        result.ObjectsBySha.Should().NotBeNull("Objects dictionary should not be null");
        result.ObjectsBySha.Count.Should().BeGreaterThan(0, "Should contain at least some objects");

        // Verify we have the commit
        result.ObjectsBySha.Should().ContainKey(commitSha, "Should contain the requested commit");
        var commitObject = result.ObjectsBySha[commitSha];
        commitObject.Type.Should().Be(PackFile.ObjectType.Commit, "The requested SHA should be a commit");

        // Parse the commit to verify it has a tree
        var commit = GitObjects.ParseCommit(commitObject.Data);
        commit.TreeSHA1.Should().NotBeNullOrEmpty("Commit should have a tree SHA");

        // Verify we have the tree
        result.ObjectsBySha.Should().ContainKey(commit.TreeSHA1, "Should contain the commit's tree");
        var treeObject = result.ObjectsBySha[commit.TreeSHA1];
        treeObject.Type.Should().Be(PackFile.ObjectType.Tree, "The tree SHA should point to a tree object");

        // Verify NO blobs are included (since it's blobless)
        var blobCount = result.ObjectsBySha.Values.Count(obj => obj.Type == PackFile.ObjectType.Blob);
        blobCount.Should().Be(0, "Blobless clone should not contain any blob objects");

        // Verify we have only commits and trees
        foreach (var obj in result.ObjectsBySha.Values)
        {
            (obj.Type == PackFile.ObjectType.Commit || obj.Type == PackFile.ObjectType.Tree)
                .Should().BeTrue($"Object {obj.SHA1base16} should be either a commit or tree, but was {obj.Type}");
        }
    }

    [Fact]
    public async Task FetchBloblessClone_with_depth_parameter()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";

        // Act - fetch with depth of 1
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha, depth: 1);

        // Assert
        result.Should().NotBeNull("Result should not be null");
        result.ObjectsBySha.Should().NotBeNull("Objects dictionary should not be null");
        result.ObjectsBySha.Count.Should().BeGreaterThan(0, "Should contain objects");

        // Count commits in the result
        var commitCount = result.ObjectsBySha.Values.Count(obj => obj.Type == PackFile.ObjectType.Commit);

        // With depth 1, we should get exactly 1 commit (the shallow clone)
        commitCount.Should().Be(1, "With depth 1, should fetch exactly 1 commit");
    }

    [Fact]
    public void FetchBloblessClone_synchronous_version()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";

        // Act
        var result = LoadFromUrl.FetchBloblessClone(repositoryUrl, commitSha);

        // Assert
        result.Should().NotBeNull("Result should not be null");
        result.CommitSha.Should().Be(commitSha, "Commit SHA should match");
        result.ObjectsBySha.Count.Should().BeGreaterThan(0, "Should contain objects");
    }

    [Fact]
    public async Task NavigateToSubtree_finds_subdirectory()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commit = GitObjects.ParseCommit(result.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Act - navigate to ["implement", "GitCore"]
        var subtreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            new[] { "implement", "GitCore" },
            result.ObjectsBySha);

        // Assert
        subtreeSha.Should().NotBeNullOrEmpty("Should return a valid SHA");
        result.ObjectsBySha.Should().ContainKey(subtreeSha, "Subtree SHA should exist in objects");
        result.ObjectsBySha[subtreeSha].Type.Should().Be(
            PackFile.ObjectType.Tree,
            "Navigated path should point to a tree");
    }

    [Fact]
    public async Task NavigateToSubtree_with_empty_path_returns_same_tree()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commit = GitObjects.ParseCommit(result.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Act - navigate with empty path
        var subtreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            System.Array.Empty<string>(),
            result.ObjectsBySha);

        // Assert
        subtreeSha.Should().Be(rootTreeSha, "Empty path should return the same tree SHA");
    }

    [Fact]
    public async Task NavigateToSubtree_throws_when_path_not_found()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commit = GitObjects.ParseCommit(result.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Act & Assert
        System.InvalidOperationException? exception = null;
        try
        {
            LoadFromUrl.NavigateToSubtree(
                rootTreeSha,
                new[] { "nonexistent", "path" },
                result.ObjectsBySha);
        }
        catch (System.InvalidOperationException ex)
        {
            exception = ex;
        }

        exception.Should().NotBeNull("Should throw InvalidOperationException");
        exception!.Message.Should().Contain("not found", "Should indicate path was not found");
    }

    [Fact]
    public async Task NavigateToSubtree_throws_when_path_component_is_file()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commit = GitObjects.ParseCommit(result.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Act & Assert - try to navigate through README.md as if it were a directory
        System.InvalidOperationException? exception = null;
        try
        {
            LoadFromUrl.NavigateToSubtree(
                rootTreeSha,
                new[] { "README.md", "something" },
                result.ObjectsBySha);
        }
        catch (System.InvalidOperationException ex)
        {
            exception = ex;
        }

        exception.Should().NotBeNull("Should throw InvalidOperationException");
        exception!.Message.Should().Contain("not a directory", "Should indicate path component is not a directory");
    }

    [Fact]
    public async Task GetTreeEntries_lists_files_and_directories()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commit = GitObjects.ParseCommit(result.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Act
        var entries = LoadFromUrl.GetTreeEntries(rootTreeSha, result.ObjectsBySha);

        // Assert
        entries.Should().NotBeNull("Entries should not be null");
        entries.Count.Should().BeGreaterThan(0, "Root tree should have entries");

        // Verify we have expected entries at the root
        var entryNames = entries.Select(e => e.Name).ToList();
        entryNames.Should().Contain("README.md", "Root should contain README.md");
        entryNames.Should().Contain("implement", "Root should contain implement directory");

        // Verify entry properties
        foreach (var entry in entries)
        {
            entry.Name.Should().NotBeNullOrEmpty("Entry should have a name");
            entry.SHA1.Should().NotBeNullOrEmpty("Entry should have a SHA");
            entry.Mode.Should().NotBeNullOrEmpty("Entry should have a mode");
        }
    }

    [Fact]
    public async Task GetTreeEntries_for_subdirectory()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commit = GitObjects.ParseCommit(result.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Navigate to the "implement" directory
        var implementTreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            new[] { "implement" },
            result.ObjectsBySha);

        // Act
        var entries = LoadFromUrl.GetTreeEntries(implementTreeSha, result.ObjectsBySha);

        // Assert
        entries.Should().NotBeNull("Entries should not be null");
        entries.Count.Should().BeGreaterThan(0, "implement directory should have entries");

        var entryNames = entries.Select(e => e.Name).ToList();
        entryNames.Should().Contain("GitCore", "implement should contain GitCore directory");
    }

    [Fact]
    public async Task GetTreeEntries_throws_when_sha_not_found()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var result = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var nonExistentSha = "0000000000000000000000000000000000000000";

        // Act & Assert
        System.InvalidOperationException? exception = null;
        try
        {
            LoadFromUrl.GetTreeEntries(nonExistentSha, result.ObjectsBySha);
        }
        catch (System.InvalidOperationException ex)
        {
            exception = ex;
        }

        exception.Should().NotBeNull("Should throw InvalidOperationException");
        exception!.Message.Should().Contain("not found", "Should indicate tree was not found");
    }

    [Fact]
    public async Task Complete_workflow_navigate_and_list_entries()
    {
        // This test demonstrates a complete workflow of:
        // 1. Fetching a blobless clone
        // 2. Navigating to a subdirectory
        // 3. Listing entries in that subdirectory

        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";

        // Act 1: Fetch blobless clone
        var cloneResult = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        // Act 2: Get the root tree from commit
        var commit = GitObjects.ParseCommit(cloneResult.ObjectsBySha[commitSha].Data);
        var rootTreeSha = commit.TreeSHA1;

        // Act 3: Navigate to subdirectory
        var gitCoreTreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            new[] { "implement", "GitCore" },
            cloneResult.ObjectsBySha);

        // Act 4: List entries in the subdirectory
        var entries = LoadFromUrl.GetTreeEntries(gitCoreTreeSha, cloneResult.ObjectsBySha);

        // Assert
        entries.Should().NotBeNull("Should have entries");
        entries.Count.Should().BeGreaterThan(0, "GitCore directory should contain files");

        var entryNames = entries.Select(e => e.Name).ToList();

        // These files should exist in the GitCore directory at this commit
        entryNames.Should().Contain("GitObjects.cs", "Should contain GitObjects.cs");
        entryNames.Should().Contain("LoadFromUrl.cs", "Should contain LoadFromUrl.cs");
        entryNames.Should().Contain("PackFile.cs", "Should contain PackFile.cs");

        // Verify modes - files should have mode starting with "100", directories should have "40000"
        var files = entries.Where(e => e.Mode.StartsWith("100")).ToList();
        var directories = entries.Where(e => e.Mode == "40000").ToList();

        files.Count.Should().BeGreaterThan(0, "Should have some files");
        directories.Count.Should().BeGreaterThan(0, "Should have some directories (e.g., Common)");
    }
}
