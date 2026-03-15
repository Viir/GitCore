using AwesomeAssertions;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace GitCore.IntegrationTests;

/// <summary>
/// Shared fixture that clones the repository into a temporary directory once per test class.
/// This ensures tests have full git history available, even when the CI checkout is shallow.
/// </summary>
public class ClonedRepositoryFixture : IDisposable
{
    private const string RepositoryUrl = "https://github.com/Viir/GitCore.git";

    public string RepoDirectory { get; }

    public string GitDirectory { get; }

    public ClonedRepositoryFixture()
    {
        RepoDirectory = Path.Combine(Path.GetTempPath(), "gitcore-test-clone-" + Guid.NewGuid().ToString("N")[..8]);

        RunGitCommand(Path.GetTempPath(), $"clone {RepositoryUrl} {RepoDirectory}");

        GitDirectory = Path.Combine(RepoDirectory, ".git");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RepoDirectory))
            {
                Directory.Delete(RepoDirectory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static void RunGitCommand(string workingDirectory, string arguments)
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();

            throw new InvalidOperationException(
                $"git {arguments} failed with exit code {process.ExitCode}: {stderr}");
        }
    }
}

public class LoadFromLocalFilesTests : IClassFixture<ClonedRepositoryFixture>
{
    private readonly ClonedRepositoryFixture _fixture;

    public LoadFromLocalFilesTests(ClonedRepositoryFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Runs a git command and returns the trimmed stdout.
    /// </summary>
    private static string RunGitCommand(string workingDirectory, string arguments)
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        process.ExitCode.Should().Be(0, $"git {arguments} should succeed");

        return output;
    }

    /// <summary>
    /// Runs a git command and returns the raw binary content from stdout
    /// without any text encoding or trimming.
    /// </summary>
    private static byte[] RunGitCommandBytes(string workingDirectory, string arguments)
    {
        var startInfo =
            new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

        using var process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git process");

        using var ms = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        process.ExitCode.Should().Be(0, $"git {arguments} should succeed");

        return ms.ToArray();
    }

    /// <summary>
    /// Computes the SHA256 hash of the given data and returns it as a lowercase hex string.
    /// </summary>
    private static string ComputeSha256Hex(ReadOnlyMemory<byte> data)
    {
        var hash = SHA256.HashData(data.Span);
        return Convert.ToHexStringLower(hash);
    }

    [Fact]
    public void Resolve_HEAD_matches_git_rev_parse()
    {
        var gitDir = _fixture.GitDirectory;
        var repoDir = _fixture.RepoDirectory;

        // Get HEAD SHA from GitCore
        var gitCoreSha = LoadFromLocalFiles.ResolveHead(gitDir);

        // Get HEAD SHA from git executable
        var gitExeSha = RunGitCommand(repoDir, "rev-parse HEAD");

        gitExeSha.Should().HaveLength(40, "SHA should be 40 characters");
        gitCoreSha.Should().NotBeNull("HEAD should resolve to a SHA");
        gitCoreSha.Should().Be(gitExeSha, "GitCore should resolve HEAD to the same SHA as git rev-parse");
    }

    [Fact]
    public void Resolve_branch_reference_matches_git_rev_parse()
    {
        var gitDir = _fixture.GitDirectory;
        var repoDir = _fixture.RepoDirectory;

        // Get the commit SHA for the main branch using the full ref path
        var reference = "refs/heads/main";
        var gitCoreSha = LoadFromLocalFiles.ResolveReference(gitDir, reference);

        // Get the expected SHA from git executable
        var gitExeSha = RunGitCommand(repoDir, $"rev-parse {reference}");

        gitExeSha.Should().HaveLength(40, "SHA should be 40 characters");
        gitCoreSha.Should().NotBeNull($"Branch ref {reference} should resolve to a SHA");
        gitCoreSha.Should().Be(gitExeSha, $"GitCore should resolve {reference} to the same SHA as git rev-parse");
    }

    [Fact]
    public void Load_repository_from_local_git_directory()
    {
        var gitDir = _fixture.GitDirectory;

        var repository = LoadFromLocalFiles.LoadRepository(gitDir);

        repository.Should().NotBeNull("Repository should be loaded");
        repository.Objects.Count.Should().BeGreaterThan(0, "Repository should contain objects");
    }

    [Fact]
    public void Load_tree_contents_from_known_commit()
    {
        var gitDir = _fixture.GitDirectory;

        // Use commit 0166a832097feb94bd565354b31559ccb355e0be ("parse commit properties and add API for reading commits") on main
        var commitSha = "0166a832097feb94bd565354b31559ccb355e0be";

        var treeContents = LoadFromLocalFiles.LoadTreeContentsFromCommit(gitDir, commitSha);

        treeContents.Should().NotBeNull("Tree contents should be loaded");
        treeContents.Count.Should().BeGreaterThan(0, "Tree should contain files");

        // Verify well-known files exist
        treeContents.Should().ContainKey(["README.md"]);
        treeContents.Should().ContainKey(["License.txt"]);
        treeContents.Should().ContainKey([".gitignore"]);
        treeContents.Should().ContainKey([".editorconfig"]);

        // Verify SHA256 hashes of file contents
        ComputeSha256Hex(treeContents[["README.md"]]).Should().Be(
            "24384c79928c47160aff81cf3bdef4bcc40afa20263d8f3c90f9b108a4224b0a",
            "SHA256 of README.md should match");

        ComputeSha256Hex(treeContents[["License.txt"]]).Should().Be(
            "73feb1ea61d29eb985f11d4e503766193f9dd68100f68b5e4fcdbc84210879dd",
            "SHA256 of License.txt should match");

        ComputeSha256Hex(treeContents[[".gitignore"]]).Should().Be(
            "7477f14117af7f5653b96abcaa4b1f7ff384f93e4ed690d926dd2e41b0d59bf8",
            "SHA256 of .gitignore should match");

        ComputeSha256Hex(treeContents[[".editorconfig"]]).Should().Be(
            "a45a27f78c461e8ba3f8e3e72e1cde770ddcba2e2a0e5387c9983d2d4d80ccc0",
            "SHA256 of .editorconfig should match");
    }

    [Fact]
    public void Known_commit_has_expected_properties()
    {
        var gitDir = _fixture.GitDirectory;

        // Use commit 0166a832097feb94bd565354b31559ccb355e0be ("parse commit properties and add API for reading commits") on main
        var commitSha = "0166a832097feb94bd565354b31559ccb355e0be";
        var repository = LoadFromLocalFiles.LoadRepository(gitDir);

        var commitObject = repository.GetObject(commitSha);
        commitObject.Should().NotBeNull("Known commit should exist in repository");
        commitObject!.Type.Should().Be(PackFile.ObjectType.Commit, "Object should be a commit");

        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Assert tree hash
        commit.TreeHash.Should().Be(
            "8583245c351d687b5fff3c785ab541b59ecd11b4",
            "Tree hash should match");

        // Assert parent hash
        commit.ParentHashes.Should().HaveCount(1, "This commit has exactly one parent");

        commit.ParentHashes[0].Should().Be(
            "d3cffeeb178e4051492a98884a043829406089cf",
            "Parent hash should match");

        // Assert author
        commit.Author.Name.Should().Be("Michael Rätzel", "Author name should match");
        commit.Author.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Author email should match");

        // Assert committer
        commit.Committer.Name.Should().Be("Michael Rätzel", "Committer name should match");
        commit.Committer.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Committer email should match");

        // Assert message
        commit.Message.Should().Be(
            "parse commit properties and add API for reading commits",
            "Commit message should match");
    }

    [Fact]
    public void Known_commit_tree_has_expected_sha()
    {
        var gitDir = _fixture.GitDirectory;

        // Use commit 0166a832097feb94bd565354b31559ccb355e0be ("parse commit properties and add API for reading commits") on main
        var commitSha = "0166a832097feb94bd565354b31559ccb355e0be";
        var repository = LoadFromLocalFiles.LoadRepository(gitDir);

        var commitObject = repository.GetObject(commitSha)!;
        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Load the tree and verify its SHA by computing it from entries
        var treeObject = repository.GetObject(commit.TreeHash);
        treeObject.Should().NotBeNull("Tree object should exist in repository");
        treeObject!.Type.Should().Be(PackFile.ObjectType.Tree, "Object should be a tree");

        var tree = GitObjects.ParseTree(treeObject.Data);

        // Compute the SHA from tree entries and verify it matches
        var computedTreeSha = LoadFromLocalFiles.ComputeTreeSha(tree.Entries);

        computedTreeSha.Should().Be(
            "8583245c351d687b5fff3c785ab541b59ecd11b4",
            "Computed tree SHA should match the expected tree hash");
    }

    [Fact]
    public void Load_tree_contents_from_head()
    {
        var gitDir = _fixture.GitDirectory;
        var repoDir = _fixture.RepoDirectory;

        var treeContents = LoadFromLocalFiles.LoadTreeContentsFromHead(gitDir);

        treeContents.Should().NotBeNull("Tree contents should be loaded from HEAD");
        treeContents.Count.Should().BeGreaterThan(0, "Tree should contain files");

        // HEAD should always have these core files
        treeContents.Should().ContainKey(["README.md"]);
        treeContents.Should().ContainKey(["License.txt"]);

        // Verify SHA256 hashes of file contents match git executable output
        foreach (var fileName in new[] { "README.md", "License.txt" })
        {
            var gitContent = RunGitCommandBytes(repoDir, $"show HEAD:{fileName}");
            var expectedSha256 = Convert.ToHexStringLower(SHA256.HashData(gitContent));

            ComputeSha256Hex(treeContents[[fileName]]).Should().Be(
                expectedSha256,
                $"SHA256 of {fileName} from GitCore should match git show output");
        }
    }

    [Fact]
    public void FindGitDirectoryUpwards_from_repository_root_finds_git_directory()
    {
        var repoDir = _fixture.RepoDirectory;

        var result = LoadFromLocalFiles.FindGitDirectoryUpwards(repoDir, out var checkedPaths);

        result.Should().NotBeNull("Should find the .git directory");
        result.Should().Be(_fixture.GitDirectory, "Should return the .git directory path");
        checkedPaths.Should().HaveCount(1, "Should check exactly one path when starting from the repo root");
    }

    [Fact]
    public void FindGitDirectoryUpwards_from_subdirectory_finds_git_directory()
    {
        var repoDir = _fixture.RepoDirectory;

        // Create a nested subdirectory inside the clone
        var subDir = Path.Combine(repoDir, "sub", "nested");
        Directory.CreateDirectory(subDir);

        var result = LoadFromLocalFiles.FindGitDirectoryUpwards(subDir, out var checkedPaths);

        result.Should().NotBeNull("Should find .git from a subdirectory");
        result.Should().Be(_fixture.GitDirectory, "Should find the correct .git directory");
        checkedPaths.Count.Should().BeGreaterThan(1,
            "Should have checked more than one path when starting below the repo root");
    }

    [Fact]
    public void FindGitDirectoryUpwards_from_file_finds_git_directory()
    {
        var repoDir = _fixture.RepoDirectory;

        // Use a known file inside the repository
        var filePath = Path.Combine(repoDir, "README.md");

        var result = LoadFromLocalFiles.FindGitDirectoryUpwards(filePath, out var checkedPaths);

        result.Should().NotBeNull("Should find the .git directory when starting from a file");
        result.Should().Be(_fixture.GitDirectory);
        checkedPaths.Should().NotBeEmpty();
    }

    [Fact]
    public void FindGitDirectoryUpwards_nonexistent_path_returns_null()
    {
        var nonexistent = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N"));

        var result = LoadFromLocalFiles.FindGitDirectoryUpwards(nonexistent, out var checkedPaths);

        result.Should().BeNull("Should return null for a nonexistent path");
        checkedPaths.Should().BeEmpty("Should not check any paths when start path does not exist");
    }

    [Fact]
    public void FindGitDirectoryUpwards_empty_git_directory_is_skipped()
    {
        // Create a temporary directory with an empty .git subdirectory
        var tempDir = Path.Combine(Path.GetTempPath(), "git-test-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

            var result = LoadFromLocalFiles.FindGitDirectoryUpwards(tempDir, out var checkedPaths);

            // The empty .git directory should have been checked but not returned
            checkedPaths.Should().Contain(Path.Combine(tempDir, ".git"),
                "Should have checked the empty .git directory");
            result.Should().NotBe(Path.Combine(tempDir, ".git"),
                "Should not return an empty .git directory");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
