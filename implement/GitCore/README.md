# GitCore

Pure managed C# implementation for reading files from Git repositories.

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

