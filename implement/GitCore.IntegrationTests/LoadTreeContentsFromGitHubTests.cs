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

    [Fact]
    public async Task Load_subdirectory_tree_contents()
    {
        // Test loading a subdirectory from a specific commit
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var subdirectoryPath = new[] { "implement", "GitCore" };

        // Load the subdirectory contents
        var subdirectoryContents = await LoadFromUrl.LoadSubdirectoryContentsFromGitUrlAsync(
            repositoryUrl, commitSha, subdirectoryPath);

        // Verify that the subdirectory was loaded successfully
        subdirectoryContents.Should().NotBeNull("Subdirectory should be loaded");
        subdirectoryContents.Count.Should().BeGreaterThan(0, "Subdirectory should contain files");

        // Verify specific files exist and have expected content using SHA256 hashes

        // Check GitObjects.cs
        var gitObjectsFile = subdirectoryContents[["GitObjects.cs"]];

        gitObjectsFile.Length.Should().BeGreaterThan(0, "GitObjects.cs should exist and have content");

        var gitObjectsSHA256 = System.Security.Cryptography.SHA256.HashData(gitObjectsFile.Span);
        var gitObjectsSHA256Hex = System.Convert.ToHexStringLower(gitObjectsSHA256);

        gitObjectsSHA256Hex.Should().Be("36f2b12feedab28573517f04f8a6e79349c35dd912d4e308979c5a01325d67d3",
            "GitObjects.cs should have the expected content");

        // Check README.md in subdirectory
        var readmeFile = subdirectoryContents[["README.md"]];

        readmeFile.Length.Should().BeGreaterThan(0, "README.md should exist in subdirectory");

        var readmeSHA256 = System.Security.Cryptography.SHA256.HashData(readmeFile.Span);
        var readmeSHA256Hex = System.Convert.ToHexStringLower(readmeSHA256);

        readmeSHA256Hex.Should().Be("df5b3681431113fac02803118d1f7298f65340716041b0fa9255de3d7d650ab7",
            "README.md should have the expected content");

        // Check LoadFromUrl.cs
        var loadFromUrlFile = subdirectoryContents[["LoadFromUrl.cs"]];

        loadFromUrlFile.Length.Should().BeGreaterThan(0, "LoadFromUrl.cs should exist");

        var loadFromUrlSHA256 = System.Security.Cryptography.SHA256.HashData(loadFromUrlFile.Span);
        var loadFromUrlSHA256Hex = System.Convert.ToHexStringLower(loadFromUrlSHA256);

        loadFromUrlSHA256Hex.Should().Be("b99815cf9e36ed9ef8f42f653e0ca370f05d5c1c2ac0733887318ac65b344e41",
            "LoadFromUrl.cs should have the expected content");

        // Check a file in the Common subdirectory
        var enumerableExtFile = subdirectoryContents[["Common", "EnumerableExtensions.cs"]];

        enumerableExtFile.Length.Should().BeGreaterThan(0, "Common/EnumerableExtensions.cs should exist");

        var enumerableExtSHA256 = System.Security.Cryptography.SHA256.HashData(enumerableExtFile.Span);
        var enumerableExtSHA256Hex = System.Convert.ToHexStringLower(enumerableExtSHA256);

        enumerableExtSHA256Hex.Should().Be("562069c4dc418f9e56c09be5f23894b50099747b9a044c073c0a41141846725d",
            "Common/EnumerableExtensions.cs should have the expected content");
    }

    // Helper class for tracking HTTP requests
    private class RequestCountingHandler(System.Net.Http.HttpMessageHandler innerHandler)
        : System.Net.Http.DelegatingHandler(innerHandler)
    {
        public int RequestCount { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            RequestCount++;
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
