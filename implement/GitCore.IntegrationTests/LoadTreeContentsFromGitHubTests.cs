using AwesomeAssertions;
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
    public void Placeholder()
    {
        /*
         * Avoid "Zero tests ran" error in CI as long as there are no real tests yet.
         * */
    }
}
