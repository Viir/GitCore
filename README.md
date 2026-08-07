# GitCore

Pure managed C# implementation for reading from Git repositories.

## Features

+ Portable and simple
  + No dependencies on native code.
+ Reading from local Git repositories
  + Resolve HEAD and other references to commit SHAs.
  + Load all files from any commit's tree.
  + Load files from a specific subdirectory within a commit's tree.
  + Supports both loose objects and pack files.
  + Reads indexed packs with 64-bit offsets without loading complete packs.
  + Supports worktrees, bare repositories, linked worktrees, and alternate object stores.
  + Incremental tree traversal with early file and subtree pruning.
+ Cloning via [Git Smart HTTP](https://git-scm.com/book/en/v2/Git-on-the-Server-Smart-HTTP)
  + Efficient partial cloning of subdirectories.
  + Configurable API for caching git objects to make cloning more efficient.

## Usage

NuGet: <https://www.nuget.org/packages/GitCore/>

```
dotnet  add  package  GitCore
```

### Load files from a local Git repository

```csharp
var gitDir = Path.Combine(repoRootDir, ".git");

// Load all files from the current HEAD commit
var filesAtHead = GitCore.LoadFromLocalFiles.LoadTreeContentsFromHead(gitDir);

// Or resolve HEAD and load from a specific commit
var commitSha = GitCore.LoadFromLocalFiles.ResolveHead(gitDir);
var filesAtCommit = GitCore.LoadFromLocalFiles.LoadTreeContentsFromCommit(gitDir, commitSha);

// Load only files under a specific subdirectory (paths relative to subdirectory)
var subdirFiles =
    GitCore.LoadFromLocalFiles.LoadSubdirectoryContentsFromHead(
        gitDir,
        ["implement", "Pine.Core"]);

// Or from a specific commit
var subdirFilesAtCommit =
    GitCore.LoadFromLocalFiles.LoadSubdirectoryContentsFromCommit(
        gitDir,
        commitSha,
        ["implement", "Pine.Core"]);

// Resolve any reference (branch, tag, etc.)
var branchSha = GitCore.LoadFromLocalFiles.ResolveReference(gitDir, "refs/heads/main");

// Load the full in-memory object store
var repository = GitCore.LoadFromLocalFiles.LoadRepository(gitDir);
```

### Incrementally traverse a local repository

`LocalGitRepository` owns pack handles and bounded caches, so keep it open while resolving related
objects and dispose it afterward. Selection runs before a subtree or blob is loaded.

```csharp
using var repository =
    GitCore.LocalGitRepository.Open(
        repoRootDir,
        new GitCore.LocalRepositoryOptions
        {
            MaximumCachedObjectBytes = 128 * 1024 * 1024,
            MaximumMaterializedObjectSize = 64 * 1024 * 1024
        });

var commitId =
    repository.ResolveHead()
    ?? throw new InvalidOperationException("HEAD cannot be resolved.");

await foreach (
    var file in repository.EnumerateTreeAsync(
        commitId,
        new GitCore.TreeTraversalOptions
        {
            SelectFile =
                file =>
                    file.Name is ".order" ||
                    file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        ? GitCore.TreeFileSelection.Include
                        : GitCore.TreeFileSelection.Skip,
            SelectSubtree =
                subtree =>
                    subtree.Path.Contains(".attachments", StringComparer.OrdinalIgnoreCase)
                        ? GitCore.TreeSubtreeSelection.Skip
                        : GitCore.TreeSubtreeSelection.Descend
        }))
{
    await using var destination = CreateDestination(file.Path);
    await file.CopyContentToAsync(destination);
}
```

`LookupObject` returns `Found`, `Missing`, or `MissingPromised` together with a typed,
context-rich error. GitCore never fetches a missing object implicitly. Applications can opt into
`FetchMissing` or `Custom` and supply a `MissingObjectProvider`, retaining control of networking,
credentials, cancellation, and persistence. Storage and limit failures use typed exceptions such
as `GitObjectNotFoundException`, `InvalidPackIndexException`, `InvalidPackObjectException`,
`GitObjectSizeLimitException`, and `GitResourceLimitException`; each exposes a `GitErrorContext`.

### Load files from a remote Git repository

```csharp
var subdirectoryContents =
    await GitCore.LoadFromUrl.LoadSubdirectoryContentsFromGitUrlAsync(
        gitUrl: "https://github.com/pine-vm/pine.git",
        commitSha: "c837c8199f38aab839c40019a50055e16d100c74",
        subdirectoryPath: ["guide"]);
```

## History

In the past, I had used LibGit2Sharp to clone Git repositories and read their files.
That often works, but the native dependencies of such a solution have caused [many](https://github.com/pine-vm/pine/commit/ba6abfc96a31d5eb87e2345a06d4854778ba80c3) [problems](https://github.com/pine-vm/pine/commit/1c7d3e47f6b847b5302eed07d27c4b3e624f15b8).

For any app that's hosted in .NET anyway, a pure managed implementation seems the natural way to simplify builds and operations.
