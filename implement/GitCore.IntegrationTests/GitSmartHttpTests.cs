using AwesomeAssertions;
using System.Threading.Tasks;
using Xunit;

namespace GitCore.IntegrationTests
{
    public class GitSmartHttpTests
    {
        [Fact]
        public async Task FetchSymbolicRefTargetAsync_returns_branch_for_head()
        {
            var gitUrl = "https://github.com/Viir/GitCore.git";

            var headTarget =
                await GitSmartHttp.FetchSymbolicRefTargetAsync(
                    gitUrl: gitUrl,
                    symbolicRef: "HEAD");

            headTarget.Should().Be("refs/heads/main", "Remote HEAD should point to the main branch");
        }

        [Fact]
        public async Task FetchSymbolicRefTargetAsync_overload_accepts_base_url_components()
        {
            var headTarget =
                await GitSmartHttp.FetchSymbolicRefTargetAsync(
                    baseUrl: "https://github.com",
                    owner: "Viir",
                    repo: "GitCore",
                    symbolicRef: "HEAD");

            headTarget.Should().Be("refs/heads/main", "Remote HEAD should point to the main branch");
        }
    }

}
