using AwesomeAssertions;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GitCore.IntegrationTests;

public class LoadFromGitHubTests
{
    [Fact]
    public async Task Load_tree_at_root_via_commit()
    {
        var treeContents =
            await LoadFromUrl.LoadTreeContentsFromUrlAsync(
                "https://github.com/Viir/GitCore/tree/14eb05f5beac67cdf2a229394baa626338a3d92e");

        var readmeFile = treeContents[["README.md"]];

        var readmeSHA256 = System.Security.Cryptography.SHA256.HashData(readmeFile.Span);

        var readmeSHA256Hex = System.Convert.ToHexStringLower(readmeSHA256);

        readmeSHA256Hex.Should().Be("3ac5bef607354b0b2b30ad140d34a4f393d12bfd375f9a8b881bb2b361cb21c7");
    }

    [Fact]
    public async Task Load_tree_at_root_via_named_branch()
    {
        var treeContents =
            await LoadFromUrl.LoadTreeContentsFromUrlAsync(
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
    public async Task Load_tree_from_repository_url_and_commit_sha()
    {
        // Test inputs: repository URL and commit SHA
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "c3135b803587ce0b4bf8f04f089f58ca4f27015c";

        // Load the tree contents using the git URL and commit SHA
        var treeContents = await LoadFromUrl.LoadTreeContentsFromGitUrlAsync(repositoryUrl, commitSha);

        // Verify that the tree was loaded successfully
        treeContents.Should().NotBeNull("Tree should be loaded");
        treeContents.Count.Should().BeGreaterThan(0, "Tree should contain files");

        // Verify that README.md exists using the same pattern as other tests
        var readmeFile = treeContents[["README.md"]];
        readmeFile.Length.Should().BeGreaterThan(0, "README.md should exist and have content");
    }

    [Fact]
    public async Task Load_tree_with_custom_http_client_for_profiling()
    {
        // Create a custom HttpClient with a delegating handler to track requests
        var requestCounter = new RequestCountingHandler(new System.Net.Http.SocketsHttpHandler());
        using var httpClient = new System.Net.Http.HttpClient(requestCounter);

        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "c3135b803587ce0b4bf8f04f089f58ca4f27015c";

        // Load the tree contents using a custom HttpClient
        var treeContents = await LoadFromUrl.LoadTreeContentsFromGitUrlAsync(repositoryUrl, commitSha, httpClient);

        // Verify that the tree was loaded successfully
        treeContents.Should().NotBeNull("Tree should be loaded");
        treeContents.Count.Should().BeGreaterThan(0, "Tree should contain files");

        // Verify that HTTP requests were made (for profiling purposes)
        requestCounter.RequestCount.Should().BeGreaterThan(0, "HTTP requests should have been made");
    }

    [Fact]
    public void Placeholder()
    {
        /*
         * Avoid "Zero tests ran" error in CI as long as there are no real tests yet.
         * */
    }

    // Helper class for tracking HTTP requests
    private class RequestCountingHandler : System.Net.Http.DelegatingHandler
    {
        public int RequestCount { get; private set; }

        public RequestCountingHandler(System.Net.Http.HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            RequestCount++;
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
