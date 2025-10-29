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
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        // Assert
        repository.Should().NotBeNull("Repository should not be null");
        repository.Objects.Should().NotBeNull("Objects dictionary should not be null");
        repository.Objects.Count.Should().BeGreaterThan(0, "Should contain at least some objects");

        // Verify we have the commit
        repository.ContainsObject(commitSha).Should().BeTrue("Should contain the requested commit");
        var commitObject = repository.GetObject(commitSha);
        commitObject.Should().NotBeNull("Commit object should not be null");
        commitObject!.Type.Should().Be(PackFile.ObjectType.Commit, "The requested SHA should be a commit");

        // Parse the commit to verify it has a tree
        var commit = GitObjects.ParseCommit(commitObject.Data);
        commit.TreeHash.Should().NotBeNullOrEmpty("Commit should have a tree SHA");

        // Verify we have the tree
        repository.ContainsObject(commit.TreeHash).Should().BeTrue("Should contain the commit's tree");
        var treeObject = repository.GetObject(commit.TreeHash);
        treeObject.Should().NotBeNull("Tree object should not be null");
        treeObject!.Type.Should().Be(PackFile.ObjectType.Tree, "The tree SHA should point to a tree object");

        // Verify NO blobs are included (since it's blobless)
        var blobCount = repository.Objects.Values.Count(obj => obj.Type == PackFile.ObjectType.Blob);
        blobCount.Should().Be(0, "Blobless clone should not contain any blob objects");

        // Verify we have only commits and trees
        foreach (var obj in repository.Objects.Values)
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
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha, depth: 1);

        // Assert
        repository.Should().NotBeNull("Repository should not be null");
        repository.Objects.Should().NotBeNull("Objects dictionary should not be null");
        repository.Objects.Count.Should().BeGreaterThan(0, "Should contain objects");

        // Count commits in the result
        var commitCount = repository.Objects.Values.Count(obj => obj.Type == PackFile.ObjectType.Commit);

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
        var repository = LoadFromUrl.FetchBloblessClone(repositoryUrl, commitSha);

        // Assert
        repository.Should().NotBeNull("Repository should not be null");
        repository.Objects.Count.Should().BeGreaterThan(0, "Should contain objects");
    }

    [Fact]
    public async Task NavigateToSubtree_finds_subdirectory()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Act - navigate to ["implement", "GitCore"]
        var subtreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            new[] { "implement", "GitCore" },
            repository);

        // Assert
        subtreeSha.Should().NotBeNullOrEmpty("Should return a valid SHA");
        repository.ContainsObject(subtreeSha).Should().BeTrue("Subtree SHA should exist in objects");
        var subtreeObject = repository.GetObject(subtreeSha);
        subtreeObject!.Type.Should().Be(
            PackFile.ObjectType.Tree,
            "Navigated path should point to a tree");
    }

    [Fact]
    public async Task NavigateToSubtree_with_empty_path_returns_same_tree()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Act - navigate with empty path
        var subtreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            System.Array.Empty<string>(),
            repository);

        // Assert
        subtreeSha.Should().Be(rootTreeSha, "Empty path should return the same tree SHA");
    }

    [Fact]
    public async Task NavigateToSubtree_throws_when_path_not_found()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Act & Assert
        System.InvalidOperationException? exception = null;
        try
        {
            LoadFromUrl.NavigateToSubtree(
                rootTreeSha,
                new[] { "nonexistent", "path" },
                repository);
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
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Act & Assert - try to navigate through README.md as if it were a directory
        System.InvalidOperationException? exception = null;
        try
        {
            LoadFromUrl.NavigateToSubtree(
                rootTreeSha,
                new[] { "README.md", "something" },
                repository);
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
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Act
        var entries = LoadFromUrl.GetTreeEntries(rootTreeSha, repository);

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
            entry.HashBase16.Should().NotBeNullOrEmpty("Entry should have a SHA");
            entry.Mode.Should().NotBeNullOrEmpty("Entry should have a mode");
        }
    }

    [Fact]
    public async Task GetTreeEntries_for_subdirectory()
    {
        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Navigate to the "implement" directory
        var implementTreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            new[] { "implement" },
            repository);

        // Act
        var entries = LoadFromUrl.GetTreeEntries(implementTreeSha, repository);

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
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        var nonExistentSha = "0000000000000000000000000000000000000000";

        // Act & Assert
        System.InvalidOperationException? exception = null;
        try
        {
            LoadFromUrl.GetTreeEntries(nonExistentSha, repository);
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
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, commitSha);

        // Act 2: Get the root tree from commit
        var commitObject = repository.GetObject(commitSha);
        var commit = GitObjects.ParseCommit(commitObject!.Data);
        var rootTreeSha = commit.TreeHash;

        // Act 3: Navigate to subdirectory
        var gitCoreTreeSha = LoadFromUrl.NavigateToSubtree(
            rootTreeSha,
            new[] { "implement", "GitCore" },
            repository);

        // Act 4: List entries in the subdirectory
        var entries = LoadFromUrl.GetTreeEntries(gitCoreTreeSha, repository);

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

    [Fact]
    public async Task Navigate_commit_parent_chain_and_verify_commit_metadata()
    {
        // This test navigates the chain of first parents, four commits deep,
        // and asserts equality for commit properties like parents, time, message, author, committer

        // Arrange
        var repositoryUrl = "https://github.com/Viir/GitCore.git";

        // Starting from a known commit with at least 4 commits in the parent chain
        // Using commit: 377a8477cff1f2c40108634b524dcf80a3e41db1 (has parent e912ee56...)
        var startCommitSha = "377a8477cff1f2c40108634b524dcf80a3e41db1";

        // Fetch with unlimited depth to get the full commit history
        var repository = await LoadFromUrl.FetchBloblessCloneAsync(repositoryUrl, startCommitSha, depth: null);

        // Act & Assert - Navigate 4 commits deep through first parents
        var currentSha = startCommitSha;

        for (var i = 0; i < 4; i++)
        {
            // Get the commit object
            var commitObject = repository.GetObject(currentSha);
            commitObject.Should().NotBeNull($"Commit {i} should exist in repository");
            commitObject!.Type.Should().Be(PackFile.ObjectType.Commit, $"Object {currentSha} should be a commit");

            // Parse the commit
            var commit = GitObjects.ParseCommit(commitObject.Data);

            // Verify commit has required properties
            commit.TreeHash.Should().NotBeNullOrEmpty($"Commit {i} should have a tree SHA");
            commit.Message.Should().NotBeNullOrEmpty($"Commit {i} should have a message");

            // Verify author properties
            commit.Author.Should().NotBeNull($"Commit {i} should have an author");
            commit.Author.Name.Should().NotBeNullOrEmpty($"Commit {i} author should have a name");
            commit.Author.Email.Should().NotBeNullOrEmpty($"Commit {i} author should have an email");
            commit.Author.Timestamp.Should().NotBe(default(System.DateTimeOffset), $"Commit {i} author should have a valid timestamp");

            // Verify committer properties
            commit.Committer.Should().NotBeNull($"Commit {i} should have a committer");
            commit.Committer.Name.Should().NotBeNullOrEmpty($"Commit {i} committer should have a name");
            commit.Committer.Email.Should().NotBeNullOrEmpty($"Commit {i} committer should have an email");
            commit.Committer.Timestamp.Should().NotBe(default(System.DateTimeOffset), $"Commit {i} committer should have a valid timestamp");

            // Log commit information for debugging
            System.Console.WriteLine($"Commit {i}: {currentSha}");
            System.Console.WriteLine($"  Message: {commit.Message.Split('\n')[0]}");
            System.Console.WriteLine($"  Author: {commit.Author.Name} <{commit.Author.Email}>");
            System.Console.WriteLine($"  Author Timestamp: {commit.Author.Timestamp}");
            System.Console.WriteLine($"  Committer: {commit.Committer.Name} <{commit.Committer.Email}>");
            System.Console.WriteLine($"  Committer Timestamp: {commit.Committer.Timestamp}");
            System.Console.WriteLine($"  Parents: {commit.ParentHashes.Count}");

            // For the first commit, verify specific expected values
            if (i == 0)
            {
                currentSha.Should().Be("377a8477cff1f2c40108634b524dcf80a3e41db1", "First commit should be the start commit");
                commit.ParentHashes.Count.Should().Be(1, "First commit should have 1 parent");
                commit.ParentHashes[0].Should().Be("e912ee56e0686099a82aeb05796e46e5e0ba40e9", "First parent should match expected SHA");
                commit.Message.Should().StartWith("Refactor API based on feedback", "Message should match expected text");
                commit.Author.Name.Should().Contain("copilot", "Author name should contain 'copilot'");
                commit.Author.Email.Should().Contain("Copilot@users.noreply.github.com", "Author email should match");
            }

            // Navigate to first parent for next iteration
            if (i < 3) // Only navigate if we haven't reached the 4th commit yet
            {
                commit.ParentHashes.Should().NotBeEmpty($"Commit {i} should have at least one parent to continue navigation");
                currentSha = commit.ParentHashes[0]; // Follow first parent
            }
        }
    }
}
