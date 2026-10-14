using AwesomeAssertions;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace GitCore.UnitTests;

public class GitWorkingTreeTests
{
    [Theory]
    [InlineData("src/main.cs", true)]
    [InlineData("new.cs", true)]
    [InlineData("obj/generated.cs", false)]
    [InlineData("obj/tracked.cs", true)]
    [InlineData("missing.cs", false)]
    [InlineData("src", false)]
    [InlineData(".git/config", false)]
    [InlineData("nested/file.cs", false)]
    [InlineData("submodule/file.cs", false)]
    [InlineData("link.cs", false)]
    [InlineData("linkdir/file.cs", false)]
    public void Working_tree_selects_only_regular_tracked_or_unignored_files(string path, bool expected)
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "obj/\n");
        storage.AddFile("/repo/.git/index", BuildIndex("src/main.cs", "obj/tracked.cs"));
        storage.AddFile("/repo/src/main.cs", "");
        storage.AddFile("/repo/new.cs", "");
        storage.AddFile("/repo/obj/generated.cs", "");
        storage.AddFile("/repo/obj/tracked.cs", "");
        storage.AddFile("/repo/.git/config", "");
        storage.AddFile("/repo/nested/.git/HEAD", "");
        storage.AddFile("/repo/nested/file.cs", "");
        storage.AddFile("/repo/submodule/.git", "gitdir: ../.git/modules/submodule\n");
        storage.AddFile("/repo/submodule/file.cs", "");
        storage.AddLink("/repo/link.cs");
        storage.AddFile("/repo/linkdir/file.cs", "");
        storage.AddLink("/repo/linkdir");

        var tree = GitWorkingTree.Open("/repo", storage);

        tree.ShouldIncludeFile(path).Should().Be(expected);
        tree.ShouldIncludeFile(Path.GetFullPath("/repo/" + path)).Should().Be(expected);
    }

    [Fact]
    public void Working_tree_keeps_ignore_queries_separate_from_tracking()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "*.cs\n");
        storage.AddFile("/repo/.git/index", BuildIndex("tracked.cs", "deleted.cs"));
        storage.AddFile("/repo/tracked.cs", "");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IsIgnored("tracked.cs").Should().BeTrue();
        tree.IsTracked("tracked.cs").Should().BeTrue();
        tree.ShouldIncludeFile("tracked.cs").Should().BeTrue();
        tree.IsTracked("deleted.cs").Should().BeTrue();
        tree.ShouldIncludeFile("deleted.cs").Should().BeFalse();
        tree.IsTracked("untracked.cs").Should().BeFalse();
        tree.IsIgnored("untracked.cs").Should().BeTrue();
    }

    [Fact]
    public void Working_tree_tracking_snapshot_can_be_refreshed_after_staging()
    {
        var storage = CreateStorage();
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.ReadIndex().Entries.Should().BeEmpty();
        storage.AddFile("/repo/.git/index", BuildIndex("added.cs"));
        tree.IsTracked("added.cs").Should().BeFalse();
        tree.RefreshIndex();
        tree.IsTracked("added.cs").Should().BeTrue();
        storage.AddFile("/repo/.git/index", BuildIndex());
        tree.RefreshIndex();
        tree.IsTracked("added.cs").Should().BeFalse();
    }

    [Theory]
    [InlineData("root.log", true)]
    [InlineData("keep.log", false)]
    [InlineData("src/local.log", true)]
    [InlineData("src/keep.log", false)]
    [InlineData("src/deep/keep.log", true)]
    [InlineData("src/deep/main.cs", false)]
    [InlineData("src/deep/from-global.tmp", true)]
    [InlineData("from-info.tmp", false)]
    [InlineData("from-root.tmp", true)]
    public void Working_tree_applies_ignore_source_and_directory_precedence(string path, bool expected)
    {
        var storage = CreateStorage();
        storage.AddFile("/home/test/.config/git/ignore", "*.log\nfrom-global.tmp\nfrom-info.tmp\nfrom-root.tmp\n");
        storage.AddFile("/repo/.git/info/exclude", "!from-global.tmp\nfrom-info.tmp\n!from-root.tmp\n");
        storage.AddFile("/repo/.gitignore", "!from-info.tmp\nfrom-root.tmp\n*.log\n!keep.log\n");
        storage.AddFile("/repo/src/.gitignore", "!keep.log\nfrom-global.tmp\n");
        storage.AddFile("/repo/src/deep/.gitignore", "keep.log\n");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IsIgnored(path).Should().Be(expected);
    }

    [Fact]
    public void Working_tree_does_not_read_rules_below_an_excluded_directory()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "blocked/\n!blocked/keep.cs\n");
        storage.AddFile("/repo/blocked/.gitignore", "!*\n");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IsIgnored("blocked/keep.cs").Should().BeTrue();
        storage.Reads.Should().NotContain(Path.GetFullPath("/repo/blocked/.gitignore"));
    }

    [Fact]
    public void Working_tree_can_reinclude_a_directory_before_consulting_its_rules()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "blocked/*\n!blocked/open/\n");
        storage.AddFile("/repo/blocked/open/.gitignore", "*.cs\n!keep.cs\n");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IsIgnored("blocked/closed/file.cs").Should().BeTrue();
        tree.IsIgnored("blocked/open/file.cs").Should().BeTrue();
        tree.IsIgnored("blocked/open/keep.cs").Should().BeFalse();
    }

    [Fact]
    public void Working_tree_ignore_files_remain_live_and_can_use_content_keyed_parser_caches()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "*.tmp\n");
        storage.AddFile("/repo/src/.gitignore", "*.tmp\n");
        var cache = new Dictionary<string, GitIgnoreFile>();
        var parses = 0;
        var tree = GitWorkingTree.Open("/repo", storage, new GitWorkingTreeOptions
        {
            ParseIgnoreFile = content =>
            {
                if (!cache.TryGetValue(content, out var parsed))
                {
                    ++parses;
                    cache[content] = parsed = GitIgnoreFile.Parse(content);
                }

                return parsed;
            }
        });

        tree.IsIgnored("src/a.cs").Should().BeFalse();
        tree.IsIgnored("src/b.cs").Should().BeFalse();
        parses.Should().Be(1);
        storage.AddFile("/repo/src/.gitignore", "*.cs\n");
        tree.IsIgnored("src/a.cs").Should().BeTrue();
        parses.Should().Be(2);
        storage.Remove("/repo/src/.gitignore");
        tree.IsIgnored("src/a.cs").Should().BeFalse();
    }

    [Fact]
    public void Working_tree_does_not_follow_symlinked_ignore_files_or_directories()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "only-directory/\n");
        storage.AddFile("/repo/src/.gitignore", "*\n");
        storage.AddLink("/repo/src/.gitignore");
        storage.AddLink("/repo/only-directory");
        storage.AddFile("/repo/linked/.gitignore", "*\n");
        storage.AddLink("/repo/linked");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IsIgnored("src/file.cs").Should().BeFalse();
        tree.IsIgnored("only-directory", isDirectory: true).Should().BeFalse();
        tree.IsIgnored("linked/file.cs").Should().BeFalse();
        storage.Reads.Should().NotContain(Path.GetFullPath("/repo/src/.gitignore"));
        storage.Reads.Should().NotContain(Path.GetFullPath("/repo/linked/.gitignore"));
    }

    [Fact]
    public void Working_tree_matches_directory_rules_for_real_and_hypothetical_directories()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.gitignore", "build/\n");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IsIgnored("build").Should().BeFalse();
        tree.IsIgnored("build", isDirectory: true).Should().BeTrue();
        storage.AddDirectory("/repo/build");
        tree.IsIgnored("build").Should().BeTrue();
        tree.IsIgnored("build/output.cs").Should().BeTrue();
        tree.IsIgnored("/repo").Should().BeFalse();
        tree.IsTracked("/repo").Should().BeFalse();
    }

    [Fact]
    public void Working_tree_discovery_finds_nearest_repository_without_filesystem_access()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/src/main.cs", "");
        storage.AddFile("/repo/nested/.git/HEAD", "");
        storage.AddFile("/repo/nested/deep/main.cs", "");

        GitWorkingTree.Discover("/repo/src/main.cs", storage)!.RootDirectory.Should().Be(Path.GetFullPath("/repo"));
        GitWorkingTree.Discover("/repo/src/new.cs", storage)!.RootDirectory.Should().Be(Path.GetFullPath("/repo"));
        GitWorkingTree.Discover("/repo/nested/deep", storage)!.RootDirectory.Should().Be(Path.GetFullPath("/repo/nested"));
        GitWorkingTree.Discover("/elsewhere/file.cs", storage).Should().BeNull();
    }

    [Fact]
    public void Working_tree_linked_worktrees_use_their_own_index_and_shared_excludes()
    {
        var storage = CreateStorage();
        storage.AddFile("/linked/.git", "gitdir: ../repo/.git/worktrees/linked\n");
        storage.AddFile("/repo/.git/worktrees/linked/commondir", "../..\n");
        storage.AddFile("/repo/.git/worktrees/linked/index", BuildIndex("linked.cs"));
        storage.AddFile("/repo/.git/index", BuildIndex("main.cs"));
        storage.AddFile("/repo/.git/info/exclude", "*.tmp\n");
        storage.AddFile("/linked/linked.cs", "");
        var tree = GitWorkingTree.Open("/linked", storage);

        tree.RootDirectory.Should().Be(Path.GetFullPath("/linked"));
        tree.CommonGitDirectory.Should().Be(Path.GetFullPath("/repo/.git"));
        tree.GitDirectory.Should().Be(Path.GetFullPath("/repo/.git/worktrees/linked"));
        tree.IsTracked("linked.cs").Should().BeTrue();
        tree.IsTracked("main.cs").Should().BeFalse();
        tree.IsIgnored("generated.tmp").Should().BeTrue();
    }

    [Fact]
    public void Working_tree_supports_absolute_gitdir_directives_and_nested_metadata_paths()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git", "gitdir: " + Path.GetFullPath("/repo/git-data") + "\n");
        storage.AddFile("/repo/git-data/index", BuildIndex());
        storage.AddFile("/repo/git-data/config", "");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.ShouldIncludeFile("git-data/config").Should().BeFalse();
    }

    [Fact]
    public void Working_tree_rejects_malformed_repositories_and_bare_repositories()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/nested/.git", "invalid\n");

        Action malformed = () => GitWorkingTree.Discover("/repo/nested", storage);
        malformed.Should().Throw<InvalidDataException>();
        storage.AddFile("/bare/HEAD", "");
        storage.AddDirectory("/bare/objects");
        Action bare = () => GitWorkingTree.Open("/bare", storage);
        bare.Should().Throw<DirectoryNotFoundException>();
        storage.AddFile("/repo/.git/commondir", "");
        Action empty = () => GitWorkingTree.Open("/repo", storage);
        empty.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("../elsewhere/file.cs")]
    [InlineData("/elsewhere/file.cs")]
    [InlineData("src/../../elsewhere/file.cs")]
    public void Working_tree_rejects_queries_outside_its_root(string path)
    {
        var tree = GitWorkingTree.Open("/repo", CreateStorage());

        Action tracked = () => tree.IsTracked(path);
        Action ignored = () => tree.IsIgnored(path);
        Action included = () => tree.ShouldIncludeFile(path);
        tracked.Should().Throw<ArgumentException>();
        ignored.Should().Throw<ArgumentException>();
        included.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Working_tree_propagates_read_errors_and_corrupt_indexes()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git/index", [1, 2, 3]);
        var tree = GitWorkingTree.Open("/repo", storage);

        Action corrupt = () => tree.IsTracked("file.cs");
        corrupt.Should().Throw<InvalidDataException>();
        storage.AddFile("/repo/.gitignore", "*\n");
        storage.ReadError = Path.GetFullPath("/repo/.gitignore");
        Action unreadable = () => tree.IsIgnored("file.cs");
        unreadable.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Working_tree_configuration_controls_case_matching_and_global_excludes()
    {
        var storage = CreateStorage();
        storage.AddFile("/home/test/global ignore", "*.global\n");
        storage.AddFile("/home/test/.gitconfig", "[core]\n excludesFile = \"~/global ignore\" # comment\n");
        storage.AddFile("/repo/.git/config", "[core]\n ignoreCase = true\n");
        storage.AddFile("/repo/.gitignore", "*.CS\n");
        storage.AddFile("/repo/.git/index", BuildIndex("Tracked.CS"));
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IgnoreCase.Should().BeTrue();
        tree.IsTracked("tracked.cs").Should().BeTrue();
        tree.IsIgnored("file.cs").Should().BeTrue();
        tree.IsIgnored("file.global").Should().BeTrue();
        var overridden = GitWorkingTree.Open("/repo", storage, new GitWorkingTreeOptions
        {
            IgnoreCase = false,
            GlobalExcludesFilePath = ""
        });
        overridden.IsTracked("tracked.cs").Should().BeFalse();
        overridden.IsIgnored("file.cs").Should().BeFalse();
        overridden.IsIgnored("file.global").Should().BeFalse();
    }

    [Fact]
    public void Working_tree_uses_the_configured_sha256_index_format()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git/config", "[extensions]\nobjectFormat = sha256\n");
        storage.AddFile("/repo/.git/index", BuildIndexEntries([("main.cs", 0x81a4u)], objectIdLength: 32));

        GitWorkingTree.Open("/repo", storage).IsTracked("main.cs").Should().BeTrue();
    }

    [Fact]
    public void Working_tree_passes_complete_raw_ignore_content_to_application_caches()
    {
        var storage = CreateStorage();
        const string Content = "\uFEFF*.tmp\r\n";
        storage.AddFile("/repo/.gitignore", Content);
        string? observed = null;
        var tree = GitWorkingTree.Open("/repo", storage, new GitWorkingTreeOptions
        {
            ParseIgnoreFile = content =>
            {
                observed = content;
                return GitIgnoreFile.Parse(content);
            }
        });

        tree.IsIgnored("file.tmp").Should().BeTrue();
        observed.Should().Be(Content);
    }

    [Fact]
    public void Working_tree_configuration_includes_obey_precedence_and_home_expansion()
    {
        var storage = CreateStorage();
        storage.AddFile("/home/test/.config/git/config", "[core]\nignoreCase = true\n");
        storage.AddFile("/home/test/.gitconfig", "[include]\npath = settings\n");
        storage.AddFile("/home/test/settings", "[core]\nexcludesFile = ~/ignore\n");
        storage.AddFile("/home/test/ignore", "*.tmp\n");
        storage.AddFile("/repo/.git/config", "[core]\nignoreCase = false\n[include]\npath = more-config\n");
        storage.AddFile("/repo/.git/more-config", "[core]\nignoreCase\n");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IgnoreCase.Should().BeTrue();
        tree.IsIgnored("file.tmp").Should().BeTrue();
    }

    [Fact]
    public void Working_tree_reads_worktree_config_only_when_enabled()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git/config.worktree", "[core]\nignoreCase = true\n");
        GitWorkingTree.Open("/repo", storage).IgnoreCase.Should().BeFalse();
        storage.AddFile("/repo/.git/config", "[extensions]\nworktreeConfig = true\n");
        GitWorkingTree.Open("/repo", storage).IgnoreCase.Should().BeTrue();
    }

    [Fact]
    public void Working_tree_configuration_detects_cycles_and_invalid_values()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git/config", "[include]\npath = config\n");
        Action cycle = () => GitWorkingTree.Open("/repo", storage);
        cycle.Should().Throw<InvalidDataException>();
        storage.AddFile("/repo/.git/config", "[core]\nignoreCase = maybe\n");
        Action invalid = () => GitWorkingTree.Open("/repo", storage);
        invalid.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Working_tree_respects_environment_selected_configuration_and_xdg_ignore_defaults()
    {
        var storage = CreateStorage();
        storage.Environment["XDG_CONFIG_HOME"] = "/settings";
        storage.Environment["GIT_CONFIG_GLOBAL"] = "/global-config";
        storage.Environment["GIT_CONFIG_SYSTEM"] = "/system-config";
        storage.AddFile("/settings/git/ignore", "*.tmp\n");
        storage.AddFile("/home/test/.gitconfig", "[core]\nexcludesFile = ~/other-ignore\n");
        storage.AddFile("/system-config", "[core]\nignoreCase = true\n");
        storage.AddFile("/global-config", "[core]\nignoreCase = false\n");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.IgnoreCase.Should().BeFalse();
        tree.IsIgnored("a.tmp").Should().BeTrue();
        storage.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        storage.Environment["GIT_CONFIG_GLOBAL"] = "";
        GitWorkingTree.Open("/repo", storage).IgnoreCase.Should().BeFalse();
    }

    [Theory]
    [InlineData("gitdir:/repo/.git", true)]
    [InlineData("gitdir:/repo/.git/", true)]
    [InlineData("gitdir:/repo/", true)]
    [InlineData("gitdir:./", true)]
    [InlineData("gitdir:/other/.git", false)]
    [InlineData("gitdir/i:/REPO/.git", true)]
    [InlineData("gitdir:repo/.git", true)]
    [InlineData("onbranch:main", true)]
    [InlineData("onbranch:feature/**", false)]
    public void Working_tree_applies_conditional_config_includes(string condition, bool expected)
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git/extra", "[core]\nignoreCase = true\n");
        storage.AddFile("/repo/.git/config", "[includeIf \"" + condition + "\"]\npath = extra\n");

        GitWorkingTree.Open("/repo", storage).IgnoreCase.Should().Be(expected);
    }

    [Fact]
    public void Working_tree_does_not_select_files_inside_indexed_submodules_without_a_git_marker()
    {
        var storage = CreateStorage();
        storage.AddFile("/repo/.git/index", BuildIndexWithMode("module", 0xe000));
        storage.AddFile("/repo/module/file.cs", "");
        var tree = GitWorkingTree.Open("/repo", storage);

        tree.ShouldIncludeFile("module/file.cs").Should().BeFalse();
    }

    private static MemoryFileSystem CreateStorage()
    {
        var storage = new MemoryFileSystem();
        storage.Environment["HOME"] = "/home/test";
        storage.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        storage.AddFile("/repo/.git/HEAD", "ref: refs/heads/main\n");
        return storage;
    }

    private static byte[] BuildIndex(params string[] paths)
    {
        return BuildIndexEntries(paths.Select(path => (path, 0x81a4u)).ToArray());
    }

    private static byte[] BuildIndexWithMode(string path, uint mode)
    {
        return BuildIndexEntries([(path, mode)]);
    }

    private static byte[] BuildIndexEntries((string Path, uint Mode)[] entries, int objectIdLength = 20)
    {
        var bytes = new List<byte>();
        bytes.AddRange("DIRC"u8.ToArray());
        WriteUInt32(bytes, 2);
        WriteUInt32(bytes, (uint)entries.Length);

        foreach (var (path, mode) in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
        {
            var start = bytes.Count;
            var name = Encoding.UTF8.GetBytes(path);
            bytes.AddRange(new byte[24]);
            WriteUInt32(bytes, mode);
            bytes.AddRange(new byte[12 + objectIdLength]);
            bytes.Add((byte)(name.Length >> 8));
            bytes.Add((byte)name.Length);
            bytes.AddRange(name);
            bytes.Add(0);

            while ((bytes.Count - start) % 8 is not 0)
                bytes.Add(0);
        }

        var content = bytes.ToArray();
        return content.Concat(objectIdLength == 20 ? SHA1.HashData(content) : SHA256.HashData(content)).ToArray();
    }

    private static void WriteUInt32(List<byte> bytes, uint value)
    {
        var encoded = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, value);
        bytes.AddRange(encoded);
    }

    private sealed class MemoryFileSystem : IGitFileSystem
    {
        private readonly Dictionary<string, GitFileSystemEntryKind> _kinds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public string? ReadError { get; set; }

        public void AddFile(string path, string content)
        {
            AddFile(path, Encoding.UTF8.GetBytes(content));
        }

        public void AddFile(string path, byte[] content)
        {
            path = Path.GetFullPath(path);
            AddDirectory(Path.GetDirectoryName(path)!);
            _files[path] = content;
            _kinds[path] = GitFileSystemEntryKind.File;
        }

        public void AddDirectory(string path)
        {
            path = Path.GetFullPath(path);
            _kinds[path] = GitFileSystemEntryKind.Directory;
            var parent = Path.GetDirectoryName(path);

            if (parent is not null)
                AddDirectory(parent);
        }

        public void AddLink(string path)
        {
            _kinds[Path.GetFullPath(path)] = GitFileSystemEntryKind.SymbolicLink;
        }

        public void Remove(string path)
        {
            _kinds.Remove(Path.GetFullPath(path));
            _files.Remove(Path.GetFullPath(path));
        }

        public GitFileSystemEntryKind GetEntryKind(string absolutePath)
        {
            return _kinds.GetValueOrDefault(absolutePath);
        }

        public byte[]? ReadAllBytes(string absolutePath)
        {
            Reads.Add(absolutePath);

            if (absolutePath == ReadError)
                throw new UnauthorizedAccessException("In-memory access error.");

            return _files.GetValueOrDefault(absolutePath);
        }

        public string? GetEnvironmentVariable(string name)
        {
            return Environment.GetValueOrDefault(name);
        }
    }
}
