using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GitCore;

internal sealed class GitWorkingTreeConfiguration
{
    public bool IgnoreCase { get; private set; }

    public string? ExcludesFile { get; private set; }

    public int ObjectIdLength { get; private set; } = 20;

    private bool _worktreeConfig;

    public static GitWorkingTreeConfiguration Read(GitWorkingTree tree, IGitFileSystem fileSystem)
    {
        var result = new GitWorkingTreeConfiguration();
        var home = fileSystem.GetEnvironmentVariable("HOME") ?? fileSystem.GetEnvironmentVariable("USERPROFILE");
        var xdg = fileSystem.GetEnvironmentVariable("XDG_CONFIG_HOME");

        if (string.IsNullOrEmpty(xdg) && !string.IsNullOrEmpty(home))
            xdg = Path.Combine(home, ".config");

        result.ExcludesFile = string.IsNullOrEmpty(xdg) ? null : Path.Combine(xdg, "git", "ignore");
        var active = new HashSet<string>(StringComparer.Ordinal);

        void ReadFile(string? path, int depth = 0)
        {
            if (string.IsNullOrEmpty(path))
                return;

            path = Path.GetFullPath(path, tree.RootDirectory);
            var bytes = fileSystem.ReadAllBytes(path);

            if (bytes is null)
                return;

            if (depth > 10 || !active.Add(path))
                throw new InvalidDataException("Git configuration includes are recursive or too deeply nested.");

            try
            {
                var section = "";
                string? subsection = null;

                foreach (var line in LogicalLines(GitWorkingTree.Decode(bytes)))
                {
                    var trimmed = line.Trim();

                    if (trimmed.Length is 0 || trimmed[0] is '#' or ';')
                        continue;

                    if (trimmed[0] is '[')
                    {
                        var end = FindSectionEnd(trimmed);

                        if (end < 0)
                            throw new InvalidDataException($"Unterminated Git configuration section in {path}.");

                        var header = trimmed[1..end].Trim();
                        var space = header.IndexOfAny([' ', '\t']);
                        section = (space < 0 ? header : header[..space]).ToLowerInvariant();
                        subsection = space < 0 ? null : DecodeValue(header[(space + 1)..].Trim());
                        continue;
                    }

                    var equals = trimmed.IndexOf('=');
                    var key = (equals < 0 ? trimmed : trimmed[..equals]).Trim().ToLowerInvariant();
                    var value = equals < 0 ? "true" : DecodeValue(trimmed[(equals + 1)..]);

                    if (section == "include" && key == "path" ||
                        section == "includeif" && key == "path" &&
                        MatchesCondition(subsection, path, tree, fileSystem, home))
                    {
                        var included = ExpandHome(value, home);
                        ReadFile(Path.GetFullPath(included, Path.GetDirectoryName(path)!), depth + 1);
                    }
                    else if (section == "core" && key == "ignorecase")
                    {
                        result.IgnoreCase = ParseBoolean(value);
                    }
                    else if (section == "core" && key == "excludesfile")
                    {
                        result.ExcludesFile = ExpandHome(value, home);
                    }
                    else if (section == "extensions" && key == "objectformat")
                    {
                        result.ObjectIdLength =
                            value.ToLowerInvariant() switch
                            {
                                "sha1" => 20,
                                "sha256" => 32,

                                _ =>
                                throw new NotSupportedException($"Unsupported Git object format {value}.")
                            };
                    }
                    else if (section == "extensions" && key == "worktreeconfig")
                    {
                        result._worktreeConfig = ParseBoolean(value);
                    }
                }
            }
            finally
            {
                active.Remove(path);
            }
        }

        if (fileSystem.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM") is not { Length: > 0 } noSystem ||
            !ParseBoolean(noSystem))
        {
            ReadFile(fileSystem.GetEnvironmentVariable("GIT_CONFIG_SYSTEM") ?? "/etc/gitconfig");
        }

        var globalConfig = fileSystem.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");

        if (globalConfig is not null)
        {
            ReadFile(globalConfig);
        }
        else
        {
            ReadFile(string.IsNullOrEmpty(xdg) ? null : Path.Combine(xdg, "git", "config"));
            ReadFile(string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".gitconfig"));
        }

        ReadFile(Path.Combine(tree.CommonGitDirectory, "config"));

        if (result._worktreeConfig)
            ReadFile(Path.Combine(tree.GitDirectory, "config.worktree"));

        return result;
    }

    private static bool MatchesCondition(
        string? condition,
        string configPath,
        GitWorkingTree tree,
        IGitFileSystem fileSystem,
        string? home)
    {
        if (condition is null)
            throw new InvalidDataException("Git includeIf requires a condition.");

        var ignoreCase = condition.StartsWith("gitdir/i:", StringComparison.OrdinalIgnoreCase);
        var prefixLength = ignoreCase ? 9 : 7;

        if (ignoreCase || condition.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = ExpandHome(condition[prefixLength..], home).Replace(Path.DirectorySeparatorChar, '/');

            if (pattern.StartsWith("./", StringComparison.Ordinal))
            {
                pattern =
                    Path.GetFullPath(pattern, Path.GetDirectoryName(configPath)!).Replace(
                        Path.DirectorySeparatorChar,
                        '/');
            }
            else if (!Path.IsPathRooted(pattern))
                pattern = "**/" + pattern;

            if (pattern.EndsWith('/'))
                pattern += "**";

            var gitPath = tree.GitDirectory.Replace(Path.DirectorySeparatorChar, '/').TrimStart('/');
            var parsed = GitIgnoreFile.Parse("/" + pattern.TrimStart('/'));

            return
                parsed.MatchPath(gitPath, true, ignoreCase) == true ||
                pattern.EndsWith("/**", StringComparison.Ordinal) &&
                parsed.MatchPath(gitPath + "/_", true, ignoreCase) == true;
        }

        if (condition.StartsWith("onbranch:", StringComparison.OrdinalIgnoreCase))
        {
            var head = fileSystem.ReadAllBytes(Path.Combine(tree.GitDirectory, "HEAD"));
            var reference = head is null ? "" : GitWorkingTree.Decode(head).Trim();
            const string BranchPrefix = "ref: refs/heads/";

            if (!reference.StartsWith(BranchPrefix, StringComparison.Ordinal))
                return false;

            var pattern = condition[9..];

            if (pattern.EndsWith('/'))
                pattern += "**";

            return GitIgnoreFile.Parse("/" + pattern).MatchPath(reference[BranchPrefix.Length..]) == true;
        }

        throw new NotSupportedException($"Unsupported Git configuration include condition {condition}.");
    }

    private static string ExpandHome(string path, string? home)
    {
        if (!path.StartsWith("~/", StringComparison.Ordinal))
            return path;

        if (string.IsNullOrEmpty(home))
            throw new InvalidDataException("Cannot expand a Git configuration path without a home directory.");

        return Path.Combine(home, path[2..]);
    }

    private static int FindSectionEnd(string line)
    {
        var quoted = false;

        for (var i = 1; i < line.Length; ++i)
        {
            if (quoted && line[i] is '\\')
            {
                ++i;
                continue;
            }

            if (line[i] is '"')
                quoted = !quoted;

            else if (!quoted && line[i] is ']')
                return i;
        }

        return -1;
    }

    private static bool ParseBoolean(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "" or "false" or "no" or "off" or "0" => false,

            _ =>
            throw new InvalidDataException($"Invalid Git configuration boolean {value}.")
        };
    }

    private static IEnumerable<string> LogicalLines(string content)
    {
        var pending = new StringBuilder();

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var slashes = 0;

            for (var i = line.Length - 1; i >= 0 && line[i] == '\\'; --i)
                ++slashes;

            if ((slashes & 1) is not 0)
            {
                pending.Append(line.AsSpan(0, line.Length - 1));
                continue;
            }

            pending.Append(line);
            yield return pending.ToString();
            pending.Clear();
        }

        if (pending.Length is not 0)
            throw new InvalidDataException("Unterminated Git configuration continuation.");
    }

    private static string DecodeValue(string raw)
    {
        var value = new StringBuilder();
        var quoted = false;
        var significantLength = 0;

        foreach (var character in DecodeCharacters(raw.TrimStart()))
        {
            if (!character.Escaped && character.Value is '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && !character.Escaped && character.Value is '#' or ';')
                break;

            value.Append(character.Value);

            if (quoted || !char.IsWhiteSpace(character.Value))
                significantLength = value.Length;
        }

        if (quoted)
            throw new InvalidDataException("Unterminated quoted Git configuration value.");

        return value.ToString(0, significantLength);
    }

    private static IEnumerable<(char Value, bool Escaped)> DecodeCharacters(string raw)
    {
        for (var i = 0; i < raw.Length; ++i)
        {
            if (raw[i] is not '\\')
            {
                yield return (raw[i], false);
                continue;
            }

            if (++i == raw.Length)
                throw new InvalidDataException("Unterminated Git configuration escape.");

            yield return (raw[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'b' => '\b',
                '\\' => '\\',
                '"' => '"',
                _ => throw new InvalidDataException($"Invalid Git configuration escape \\{raw[i]}.")
            }, true);
        }
    }
}
