# GitCore

Pure managed C# implementation for reading from Git repositories.

## Features

+ Portable and simple
  + No dependencies on native code.
  + No dependencies on file system.
+ Cloning via [Git Smart HTTP](https://git-scm.com/book/en/v2/Git-on-the-Server-Smart-HTTP)
  + Efficient partial cloning of subdirectories.
  + Configurable API for caching git objects to make cloning more efficient.

## Usage

NuGet: <https://www.nuget.org/packages/GitCore/>

```
dotnet  add  package  GitCore
```

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

