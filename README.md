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
+ Working-tree file selection for formatters and similar tools
  + Query tracked files using the staging-area index, independently of HEAD.
  + Parse and cache immutable gitignore rules, or query all applicable ignore files.
  + Substitute filesystem and environment access for testing or virtual filesystems.
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

### Select working-tree files for formatting

Use `GitWorkingTree` when processing files on disk rather than committed tree contents:

```csharp
var workingTree = GitCore.GitWorkingTree.Discover(filePath);

if (workingTree is not null && workingTree.ShouldIncludeFile(filePath))
{
    // Apply the formatter to this existing regular file.
}
```

`Open(repoRootDir)` opens a known working-tree root; `Discover(path)` searches upward
for the nearest repository and returns `null` outside one. Both support `.git`
directories and gitdir files, including linked worktrees. Queries accept absolute native
paths or paths relative to `RootDirectory`; paths outside that tree throw.

The APIs deliberately separate three questions:

| API | Meaning | Corresponding Git command |
| --- | --- | --- |
| `IsTracked(path)` | An entry exists in the current index, including staged additions, conflict stages and files deleted only on disk. | `git ls-files --error-unmatch -- path` |
| `IsIgnored(path)` | Applicable ignore rules exclude this path, **regardless of tracking**. | `git check-ignore --no-index -- path` |
| `ShouldIncludeFile(path)` | An existing regular file is tracked **or** is untracked and not ignored. | File membership in `git ls-files --cached --others --exclude-standard` |

An ignored file that is already tracked remains a formatting candidate. Directories,
missing files, symbolic links, Git metadata, and files inside nested repositories or
submodules are not candidates. This predicate does not test whether content has changed,
or reproduce all `git status` behavior (for example, assume-unchanged flags).
The caller still decides which extensions to format and enumerates the files.

Ignore evaluation supports `.gitignore` files at each directory level, the shared
`info/exclude` file, and user-wide excludes (`core.excludesFile`, or
`$XDG_CONFIG_HOME/git/ignore`, defaulting to `~/.config/git/ignore`).
Later rules override earlier rules; deeper `.gitignore` files take precedence over
parent files and repository/user excludes. An excluded parent directory must itself
be re-included before any child can be re-included. Symlinked `.gitignore` files are
not followed. `IsIgnored(path, isDirectory: true)` also supports hypothetical directories.

Repository configuration determines case sensitivity through `core.ignoreCase`.
System, user, repository and enabled worktree configuration are read, including regular
includes and `includeIf` conditions for `gitdir`, `gitdir/i`, and `onbranch`.
Other conditional include kinds throw `NotSupportedException` rather than silently
returning an incorrect result. Options can override `IgnoreCase` and
`GlobalExcludesFilePath` (an empty path disables user-wide excludes).

An instance snapshots configuration and lazily snapshots the index.
Call `RefreshIndex()` after staging changes, or open a new instance.
Ignore files are read on each query, and unchanged contents reuse their parsed form.
Instances are not intended for concurrent use. Bare repositories are not working trees.

### Parse and cache individual gitignore files

```csharp
var parsed = GitCore.GitIgnoreFile.Parse(rawGitignoreContent);
var ignored = parsed.IsIgnored("obj/Generated.cs");
```

`GitIgnoreFile` and its exposed rules are immutable, so applications can cache parsed
files by their **complete raw content**, independent of a file's location.
Paths in this API are slash-separated and relative to the directory containing that
gitignore file. It supports comments, escaping, negation, directory-only and anchored
patterns, `*`, `?`, character classes, and Git's special `**` patterns.
`IsIgnored` also checks excluded ancestors. `MatchPath` matches only the supplied
entry and returns `true` (excluded), `false` (re-included), or `null` (no matching rule),
which allows applications to compose multiple ignore sources themselves.

Supply an application-wide parsing cache to working-tree queries:

```csharp
var cache = new Dictionary<string, GitCore.GitIgnoreFile>();
var options = new GitCore.GitWorkingTreeOptions
{
    ParseIgnoreFile = content =>
    {
        if (!cache.TryGetValue(content, out var parsed))
            cache[content] = parsed = GitCore.GitIgnoreFile.Parse(content);
        return parsed;
    }
};
var workingTree = GitCore.GitWorkingTree.Open(repoRootDir, options: options);
```

`ReadIndex()` exposes the immutable staging-area snapshot and entry metadata.
`GitIndex.Parse(bytes)` also parses an index without a repository or filesystem.
The index reader validates checksums and accepts versions 2, 3 and 4; unsupported
mandatory extensions fail explicitly rather than treating tracked files as untracked.
Split indexes are resolved through the injected filesystem (`GitIndex.Parse` accepts
a shared-index resolver). SHA-1 and SHA-256 indexes are supported. Sparse-directory
entries are exposed, but queries beneath an unexpanded sparse-index directory throw
`NotSupportedException`; use a full index for those paths.

All working-tree filesystem and environment access goes through `IGitFileSystem`.
Pass a custom implementation to `Open` or `Discover` for in-memory tests; the default
is `PhysicalGitFileSystem.Instance`. Parsing and matching individual ignore files and
indexes require no filesystem access. No API here starts a Git process.

### Load files from a remote Git repository

```csharp
var subdirectoryContents =
    await GitCore.LoadFromUrl.LoadSubdirectoryContentsFromGitUrlAsync(
        gitUrl: "https://github.com/pine-vm/pine.git",
        commitSha: "c837c8199f38aab839c40019a50055e16d100c74",
        subdirectoryPath: ["guide"]);
```
