using AwesomeAssertions;
using Xunit;

namespace GitCore.UnitTests;

public class ParseTreeUrlTests
{
    [Fact]
    public void ParseTreeUrl_github_with_branch()
    {
        var url = "https://github.com/Viir/GitCore/tree/main";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://github.com");
        result.Owner.Should().Be("Viir");
        result.Repo.Should().Be("GitCore");
        result.CommitShaOrBranch.Should().Be("main");
        result.SubdirectoryPath.Should().BeNull("URL has no subdirectory");
    }

    [Fact]
    public void ParseTreeUrl_github_with_commit_sha()
    {
        var url = "https://github.com/Viir/GitCore/tree/95e147221ccae4d8609f02f132fc57f87adc135a";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://github.com");
        result.Owner.Should().Be("Viir");
        result.Repo.Should().Be("GitCore");
        result.CommitShaOrBranch.Should().Be("95e147221ccae4d8609f02f132fc57f87adc135a");
        result.SubdirectoryPath.Should().BeNull("URL has no subdirectory");
    }

    [Fact]
    public void ParseTreeUrl_github_with_branch_and_subdirectory()
    {
        var url = "https://github.com/Viir/GitCore/tree/main/implement/GitCore";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://github.com");
        result.Owner.Should().Be("Viir");
        result.Repo.Should().Be("GitCore");
        result.CommitShaOrBranch.Should().Be("main");
        result.SubdirectoryPath.Should().NotBeNull("URL has subdirectory");
        result.SubdirectoryPath.Should().HaveCount(2);
        result.SubdirectoryPath![0].Should().Be("implement");
        result.SubdirectoryPath[1].Should().Be("GitCore");
    }

    [Fact]
    public void ParseTreeUrl_github_with_commit_sha_and_subdirectory()
    {
        var url = "https://github.com/Viir/GitCore/tree/95e147221ccae4d8609f02f132fc57f87adc135a/implement/GitCore";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://github.com");
        result.Owner.Should().Be("Viir");
        result.Repo.Should().Be("GitCore");
        result.CommitShaOrBranch.Should().Be("95e147221ccae4d8609f02f132fc57f87adc135a");
        result.SubdirectoryPath.Should().NotBeNull("URL has subdirectory");
        result.SubdirectoryPath.Should().HaveCount(2);
        result.SubdirectoryPath![0].Should().Be("implement");
        result.SubdirectoryPath[1].Should().Be("GitCore");
    }

    [Fact]
    public void ParseTreeUrl_github_with_nested_subdirectory()
    {
        var url = "https://github.com/Viir/GitCore/tree/main/implement/GitCore/Common";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://github.com");
        result.Owner.Should().Be("Viir");
        result.Repo.Should().Be("GitCore");
        result.CommitShaOrBranch.Should().Be("main");
        result.SubdirectoryPath.Should().NotBeNull("URL has subdirectory");
        result.SubdirectoryPath.Should().HaveCount(3);
        result.SubdirectoryPath![0].Should().Be("implement");
        result.SubdirectoryPath[1].Should().Be("GitCore");
        result.SubdirectoryPath[2].Should().Be("Common");
    }

    [Fact]
    public void ParseTreeUrl_gitlab_with_branch()
    {
        var url = "https://gitlab.com/owner/repo/-/tree/main";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://gitlab.com");
        result.Owner.Should().Be("owner");
        result.Repo.Should().Be("repo");
        result.CommitShaOrBranch.Should().Be("main");
        result.SubdirectoryPath.Should().BeNull("URL has no subdirectory");
    }

    [Fact]
    public void ParseTreeUrl_gitlab_with_subdirectory()
    {
        var url = "https://gitlab.com/owner/repo/-/tree/main/src/test";
        var result = GitSmartHttp.ParseTreeUrl(url);

        result.BaseUrl.Should().Be("https://gitlab.com");
        result.Owner.Should().Be("owner");
        result.Repo.Should().Be("repo");
        result.CommitShaOrBranch.Should().Be("main");
        result.SubdirectoryPath.Should().NotBeNull("URL has subdirectory");
        result.SubdirectoryPath.Should().HaveCount(2);
        result.SubdirectoryPath![0].Should().Be("src");
        result.SubdirectoryPath[1].Should().Be("test");
    }
}
