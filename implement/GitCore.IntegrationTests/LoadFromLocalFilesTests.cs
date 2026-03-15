using AwesomeAssertions;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace GitCore.IntegrationTests;

public class LoadFromLocalFilesTests
{
    /// <summary>
    /// Finds the .git directory by walking up from the test assembly's base directory.
    /// </summary>
    private static string FindGitDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var gitDir = Path.Combine(directory.FullName, ".git");

            if (Directory.Exists(gitDir))
            {
                return gitDir;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find .git directory");
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
        var gitDir = FindGitDirectory();
        var repoDir = Path.GetDirectoryName(gitDir)!;

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
        var gitDir = FindGitDirectory();
        var repoDir = Path.GetDirectoryName(gitDir)!;

        // Get the current branch name from git
        var branchName = RunGitCommand(repoDir, "rev-parse --abbrev-ref HEAD");

        // Get the commit SHA for that branch using the full ref path
        var reference = $"refs/heads/{branchName}";
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
        var gitDir = FindGitDirectory();

        var repository = LoadFromLocalFiles.LoadRepository(gitDir);

        repository.Should().NotBeNull("Repository should be loaded");
        repository.Objects.Count.Should().BeGreaterThan(0, "Repository should contain objects");
    }

    [Fact]
    public void Load_tree_contents_from_known_commit()
    {
        var gitDir = FindGitDirectory();

        // Use commit 9b25d0518aab1cac4ef8add331b73fd1fd88c6d2 ("add requirements doc")
        var commitSha = "9b25d0518aab1cac4ef8add331b73fd1fd88c6d2";

        var treeContents = LoadFromLocalFiles.LoadTreeContentsFromCommit(gitDir, commitSha);

        treeContents.Should().NotBeNull("Tree contents should be loaded");
        treeContents.Count.Should().BeGreaterThan(0, "Tree should contain files");

        // Verify well-known files exist
        treeContents.Should().ContainKey(["README.md"]);
        treeContents.Should().ContainKey(["License.txt"]);
        treeContents.Should().ContainKey([".gitignore"]);
        treeContents.Should().ContainKey(["gitcore-local-repository-requirements.md"]);

        // Verify SHA256 hashes of file contents
        ComputeSha256Hex(treeContents[["README.md"]]).Should().Be(
            "9fc0061e2e133947e0da90f0ff1e3a8c8089afe4067c05672a38d621304af75b",
            "SHA256 of README.md should match");

        ComputeSha256Hex(treeContents[["License.txt"]]).Should().Be(
            "73feb1ea61d29eb985f11d4e503766193f9dd68100f68b5e4fcdbc84210879dd",
            "SHA256 of License.txt should match");

        ComputeSha256Hex(treeContents[[".gitignore"]]).Should().Be(
            "7477f14117af7f5653b96abcaa4b1f7ff384f93e4ed690d926dd2e41b0d59bf8",
            "SHA256 of .gitignore should match");

        ComputeSha256Hex(treeContents[["gitcore-local-repository-requirements.md"]]).Should().Be(
            "3ea7a8122f332f4146ab5845f37a6efe30aab697d61dcf95b74ce6adc136d6c4",
            "SHA256 of gitcore-local-repository-requirements.md should match");
    }

    [Fact]
    public void Known_commit_has_expected_properties()
    {
        var gitDir = FindGitDirectory();

        // Load commit 9b25d0518aab1cac4ef8add331b73fd1fd88c6d2
        var commitSha = "9b25d0518aab1cac4ef8add331b73fd1fd88c6d2";
        var repository = LoadFromLocalFiles.LoadRepository(gitDir);

        var commitObject = repository.GetObject(commitSha);
        commitObject.Should().NotBeNull("Known commit should exist in repository");
        commitObject!.Type.Should().Be(PackFile.ObjectType.Commit, "Object should be a commit");

        var commit = GitObjects.ParseCommit(commitObject.Data);

        // Assert tree hash
        commit.TreeHash.Should().Be(
            "ef622e944943fb7b90a0a7e6fadbe3756a439e2f",
            "Tree hash should match");

        // Assert parent hash
        commit.ParentHashes.Should().HaveCount(1, "This commit has exactly one parent");

        commit.ParentHashes[0].Should().Be(
            "abc8723a687acfef2c832b4c869fac4fdbe685e8",
            "Parent hash should match");

        // Assert author
        commit.Author.Name.Should().Be("Michael Rätzel", "Author name should match");
        commit.Author.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Author email should match");

        // Assert committer
        commit.Committer.Name.Should().Be("Michael Rätzel", "Committer name should match");
        commit.Committer.Email.Should().Be("michael@xn--michaelrtzel-ncb.com", "Committer email should match");

        // Assert message
        commit.Message.Should().Be("add requirements doc", "Commit message should match");
    }

    [Fact]
    public void Known_commit_tree_has_expected_sha()
    {
        var gitDir = FindGitDirectory();

        var commitSha = "9b25d0518aab1cac4ef8add331b73fd1fd88c6d2";
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
            "ef622e944943fb7b90a0a7e6fadbe3756a439e2f",
            "Computed tree SHA should match the expected tree hash");
    }

    [Fact]
    public void Load_tree_contents_from_head()
    {
        var gitDir = FindGitDirectory();
        var repoDir = Path.GetDirectoryName(gitDir)!;

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
}
