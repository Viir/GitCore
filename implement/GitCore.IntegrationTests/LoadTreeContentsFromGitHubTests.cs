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
    public async Task Load_subdirectory_tree_from_url_with_commit_sha()
    {
        // Test loading a subdirectory using a URL with commit SHA
        var url = "https://github.com/Viir/GitCore/tree/95e147221ccae4d8609f02f132fc57f87adc135a/implement/GitCore";

        // Load the subdirectory contents
        var subdirectoryContents = await LoadFromUrl.LoadTreeContentsFromUrlAsync(url);

        // Verify that the subdirectory was loaded successfully
        subdirectoryContents.Should().NotBeNull("Subdirectory should be loaded");
        subdirectoryContents.Count.Should().Be(9, "Subdirectory should contain 9 files");

        // Verify specific files exist in the subdirectory
        subdirectoryContents.Should().ContainKey(["GitObjects.cs"]);

        subdirectoryContents.Should().ContainKey(["LoadFromUrl.cs"]);
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
    public async Task Load_subdirectory_tree_contents()
    {
        // Test loading a subdirectory from a specific commit
        var repositoryUrl = "https://github.com/Viir/GitCore.git";
        var commitSha = "1d6d1aea461e4c831d7d2d0526da57b333b6b34e";
        var subdirectoryPath = new[] { "implement", "GitCore" };

        // Load the subdirectory contents
        var subdirectoryContents =
            await LoadFromUrl.LoadSubdirectoryContentsFromGitUrlAsync(
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

    [Fact]
    public async Task Load_relatively_small_subdirectory_from_larger_repository()
    {
        // Create a custom HttpClient with a handler to track data transfer
        var dataTrackingHandler = new DataTrackingHandler(new System.Net.Http.SocketsHttpHandler());
        using var httpClient = new System.Net.Http.HttpClient(dataTrackingHandler);

        // Target: Load the 'guide' subdirectory, which is relatively small compared to others.
        var repositoryUrl = "https://github.com/pine-vm/pine.git";
        var commitSha = "c837c8199f38aab839c40019a50055e16d100c74";
        var subdirectoryPath = new[] { "guide" };

        // Load the subdirectory contents
        var subdirectoryContents =
            await LoadFromUrl.LoadSubdirectoryContentsFromGitUrlAsync(
                repositoryUrl, commitSha, subdirectoryPath, httpClient);

        // Verify that the subdirectory was loaded successfully
        subdirectoryContents.Should().NotBeNull("Subdirectory should be loaded");
        subdirectoryContents.Count.Should().BeGreaterThan(0, "Subdirectory should contain files");

        // Verify that we have the expected files
        subdirectoryContents.Should().ContainKey(
            ["customizing-elm-app-builds-with-compilation-interfaces.md"],
            "The subdirectory should contain an 'customizing-elm-app-builds-with-compilation-interfaces.md' file");

        subdirectoryContents.Should().ContainKey(
            ["how-to-build-a-backend-app-in-elm.md"],
            "The subdirectory should contain a 'how-to-build-a-backend-app-in-elm.md' file");

        var subtreeAggregateFileContentSize =
            subdirectoryContents.Values.Sum(file => file.Length);

        // Profile data transfer
        var totalBytesReceived = dataTrackingHandler.TotalBytesReceived;
        var totalBytesSent = dataTrackingHandler.TotalBytesSent;
        var requestCount = dataTrackingHandler.RequestCount;

        // Log profiling information for debugging
        System.Console.WriteLine($"Data Transfer Profile:");
        System.Console.WriteLine($"  Total Requests: {requestCount}");
        System.Console.WriteLine($"  Total Bytes Sent: {totalBytesSent:N0} bytes");
        System.Console.WriteLine($"  Total Bytes Received: {totalBytesReceived:N0} bytes");
        System.Console.WriteLine($"  Total Data Transfer: {totalBytesSent + totalBytesReceived:N0} bytes");
        System.Console.WriteLine($"  Subdirectory Content Size: {subtreeAggregateFileContentSize:N0} bytes");
        System.Console.WriteLine($"  Files in Subdirectory: {subdirectoryContents.Count}");
        System.Console.WriteLine($"  Compression Ratio: {(double)totalBytesReceived / subtreeAggregateFileContentSize:F2}x");

        // Assert bounds on data transfer
        // With blobless clone optimization, we:
        // 1. Fetch commit + trees only (blobless pack file)
        // 2. Navigate to subdirectory and identify needed blobs
        // 3. Fetch only those specific blobs
        // This results in significantly less data transfer compared to fetching all files

        requestCount.Should().BeLessThan(10, "Should not make excessive HTTP requests");

        // Set a reasonable upper bound for data transfer with blobless optimization
        // We expect data transfer to be close to the actual content size plus some overhead
        // for trees, commit, and pack file headers.
        var maxExpectedBytes = subtreeAggregateFileContentSize * 4 + 100_000;

        totalBytesReceived.Should().BeLessThan(maxExpectedBytes,
            $"Should optimize data transfer for subdirectory (received {totalBytesReceived:N0} bytes)");
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

    // Helper class for tracking data transfer
    private class DataTrackingHandler(System.Net.Http.HttpMessageHandler innerHandler)
        : System.Net.Http.DelegatingHandler(innerHandler)
    {
        public int RequestCount { get; private set; }
        public long TotalBytesSent { get; private set; }
        public long TotalBytesReceived { get; private set; }

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            RequestCount++;

            // Track request size
            if (request.Content is not null)
            {
                var requestBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                TotalBytesSent += requestBytes.Length;
            }

            // Send the request
            var response = await base.SendAsync(request, cancellationToken);

            // Track response size
            if (response.Content is not null)
            {
                // Capture headers before reading content
                var originalHeaders = response.Content.Headers.ToList();

                var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                TotalBytesReceived += responseBytes.Length;

                // Re-wrap the content so it can be read again by the caller
                response.Content = new System.Net.Http.ByteArrayContent(responseBytes);

                // Restore the original content headers
                foreach (var header in originalHeaders)
                {
                    response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return response;
        }
    }
}
