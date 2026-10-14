using AwesomeAssertions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace GitCore.UnitTests;

public class GitIgnoreFileTests
{
    [Fact]
    public void GitIgnore_parsing_preserves_source_and_immutable_rule_metadata()
    {
        const string content = "\uFEFF# comment\r\n\r\n!/build/*.obj/  \r\nname\r\n";
        var file = GitIgnoreFile.Parse(content);

        file.Content.Should().Be(content);
        file.Rules.Should().HaveCount(2);
        var rule = file.Rules[0];
        rule.SourceLineNumber.Should().Be(3);
        rule.SourceLine.Should().Be("!/build/*.obj/  ");
        rule.Pattern.Should().Be("build/*.obj");
        rule.IsNegated.Should().BeTrue();
        rule.IsDirectoryOnly.Should().BeTrue();
        rule.HasLeadingSlash.Should().BeTrue();
        rule.IsRootAnchored.Should().BeTrue();
        rule.MatchesBasename.Should().BeFalse();
        file.Rules[1].MatchesBasename.Should().BeTrue();
        file.Rules.Should().NotBeOfType<GitIgnoreFile.Rule[]>();

        var mutableInterface = (IList<GitIgnoreFile.Rule>)file.Rules;
        mutableInterface.IsReadOnly.Should().BeTrue();
        var replace = () => mutableInterface[0] = file.Rules[1];
        replace.Should().Throw<NotSupportedException>();
        typeof(GitIgnoreFile.Rule).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
    }

    [Theory]
    [InlineData("", "file", null)]
    [InlineData("#file\n \n!\n/\n!/\n", "file", null)]
    [InlineData("#file", "#file", null)]
    [InlineData("\\#file", "#file", true)]
    [InlineData("\\!file", "!file", true)]
    [InlineData(" #file", " #file", true)]
    [InlineData(" !file", " !file", true)]
    [InlineData("\uFEFFfile\r\n", "file", true)]
    [InlineData("other\n\uFEFFfile", "\uFEFFfile", true)]
    [InlineData("file\r", "file", true)]
    [InlineData("file\rname", "file\rname", true)]
    [InlineData("file\0rest", "file", true)]
    [InlineData("file  ", "file", true)]
    [InlineData("file  ", "file ", null)]
    [InlineData("file\\ ", "file ", true)]
    [InlineData("file\\  ", "file ", true)]
    [InlineData("file \\ ", "file  ", true)]
    [InlineData("file\\\\ ", "file\\", true)]
    [InlineData("file\\\\\\ ", "file\\ ", true)]
    [InlineData("file\t", "file\t", true)]
    [InlineData("\\ ", " ", true)]
    [InlineData("one\\\ntwo", "onetwo", null)]
    [InlineData("foo\\", "foo\\", null)]
    [InlineData("literal\\*.txt", "literal*.txt", true)]
    [InlineData("literal\\*.txt", "literal-a.txt", null)]
    [InlineData("literal\\?.txt", "literal?.txt", true)]
    [InlineData("literal\\[a\\].txt", "literal[a].txt", true)]
    [InlineData("literal\\\\name", "literal\\name", true)]
    public void GitIgnore_parses_comments_escapes_and_whitespace(string content, string path, bool? expected)
    {
        GitIgnoreFile.Parse(content).MatchPath(path).Should().Be(expected);
    }

    [Theory]
    [InlineData("*.log", "file.log", false, true)]
    [InlineData("*.log", "a/b/file.log", false, true)]
    [InlineData("*.log", "a.log/file.txt", false, null)]
    [InlineData("/file.log", "file.log", false, true)]
    [InlineData("/file.log", "a/file.log", false, null)]
    [InlineData("a/file.log", "a/file.log", false, true)]
    [InlineData("a/file.log", "x/a/file.log", false, null)]
    [InlineData("/a/file.log", "a/file.log", false, true)]
    [InlineData("cache/", "a/cache", true, true)]
    [InlineData("cache/", "a/cache", false, null)]
    [InlineData("cache/", "cache/file", false, null)]
    [InlineData("/cache/", "a/cache", true, null)]
    [InlineData("/cache/", "cache", true, true)]
    [InlineData("cache/", "cache/", false, true)]
    [InlineData("cache", "cache", false, true)]
    [InlineData("cache", "cache", true, true)]
    [InlineData("a/*/c", "a/b/c", false, true)]
    [InlineData("a/*/c", "a/b/d/c", false, null)]
    [InlineData("a/?/c", "a/b/c", false, true)]
    [InlineData("a/?/c", "a/bb/c", false, null)]
    [InlineData("a/?/c", "a/b/d/c", false, null)]
    [InlineData("foo*", "foo", false, true)]
    [InlineData("*", ".hidden", false, true)]
    [InlineData("foo\\/bar", "foo/bar", false, true)]
    [InlineData("foo\\/", "foo", true, null)]
    [InlineData("[a/b]", "a", false, true)]
    [InlineData("[a/b]", "x/a", false, null)]
    [InlineData("a/**/", "a", true, null)]
    [InlineData("a/**/", "a/b", true, true)]
    [InlineData("a/**/", "a/b/c", true, true)]
    [InlineData("**/cache/", "cache", true, true)]
    [InlineData("**/cache/", "a/b/cache", true, true)]
    [InlineData("...", "...", false, true)]
    [InlineData("..name", "..name", false, true)]
    [InlineData("a:b", "a:b", false, true)]
    public void GitIgnore_matches_only_target_with_correct_anchoring(
        string pattern, string path, bool directory, bool? expected)
    {
        GitIgnoreFile.Parse(pattern).MatchPath(path, directory).Should().Be(expected);
    }

    [Theory]
    [InlineData("**/file", "file", true)]
    [InlineData("**/file", "a/b/file", true)]
    [InlineData("/**/file", "a/b/file", true)]
    [InlineData("a/**/file", "a/file", true)]
    [InlineData("a/**/file", "a/b/c/file", true)]
    [InlineData("a/**/file", "x/a/file", false)]
    [InlineData("a/**", "a", false)]
    [InlineData("a/**", "a/file", true)]
    [InlineData("a/**", "a/b/file", true)]
    [InlineData("a/**/", "a/b/file", false)]
    [InlineData("a/**/b/**/c", "a/b/c", true)]
    [InlineData("a/**/b/**/c", "a/x/y/b/z/c", true)]
    [InlineData("a/**/b/**/c", "a/x/y/b/z/d", false)]
    [InlineData("a/**b/c", "a/xxb/c", true)]
    [InlineData("a/**b/c", "a/x/b/c", false)]
    [InlineData("a/b**/c", "a/bx/c", true)]
    [InlineData("a/b**/c", "a/b/x/c", false)]
    [InlineData("a/***/c", "a/c", true)]
    [InlineData("a/***/c", "a/b/d/c", true)]
    [InlineData("a/**\\/c", "a/c", false)]
    [InlineData("a/**\\/c", "a/b/d/c", true)]
    [InlineData("a/\\*\\*/c", "a/**/c", true)]
    [InlineData("a/\\*\\*/c", "a/b/d/c", false)]
    [InlineData("**", "a/b/file", true)]
    [InlineData("**file", "a/b/file", true)]
    [InlineData("a**/file", "a/b/file", false)]
    public void GitIgnore_matches_double_star_boundaries(string pattern, string path, bool expected)
    {
        (GitIgnoreFile.Parse(pattern).MatchPath(path) is true).Should().Be(expected);
    }

    [Theory]
    [InlineData("[abc].txt", "b.txt", true)]
    [InlineData("[abc].txt", "d.txt", false)]
    [InlineData("[a-c].txt", "b.txt", true)]
    [InlineData("[a-cx-z].txt", "y.txt", true)]
    [InlineData("[!a-c].txt", "d.txt", true)]
    [InlineData("[!a-c].txt", "b.txt", false)]
    [InlineData("[^a-c].txt", "d.txt", true)]
    [InlineData("[^a-c].txt", "a.txt", false)]
    [InlineData("[]a]", "]", true)]
    [InlineData("[]-a]", "_", true)]
    [InlineData("[!]]", "x", true)]
    [InlineData("[!]]", "]", false)]
    [InlineData("[-a]", "-", true)]
    [InlineData("[a-]", "-", true)]
    [InlineData("[a\\-z]", "-", true)]
    [InlineData("[a\\-z]", "m", false)]
    [InlineData("[\\]]", "]", true)]
    [InlineData("[\\\\]", "x", false)]
    [InlineData("x[\\\\]", "x\\", true)]
    [InlineData("[z-a]", "m", false)]
    [InlineData("[z-a]", "z", true)]
    [InlineData("[z-a]", "a", false)]
    [InlineData("[[:bad]]", "f]", false)]
    [InlineData("[[:bad]]", "b]", true)]
    [InlineData("[[:bad]]", "[]", true)]
    [InlineData("[[:bad]]", ":]", true)]
    [InlineData("[a-[:digit:]]", "a]", true)]
    [InlineData("[abc", "[abc", false)]
    [InlineData("[]", "[]", false)]
    [InlineData("[[:unknown:]]", "x", false)]
    [InlineData("[[:digit:]]", "4", true)]
    [InlineData("[[:digit:]]", "a", false)]
    [InlineData("[[:alpha:][:digit:]_]", "_", true)]
    [InlineData("[[:alnum:]]", "z", true)]
    [InlineData("[[:alpha:]]", "A", true)]
    [InlineData("[[:blank:]]", "\t", true)]
    [InlineData("[[:blank:]]", "\r", false)]
    [InlineData("[[:cntrl:]]", "\u007f", true)]
    [InlineData("[[:graph:]]", " ", false)]
    [InlineData("[[:graph:]]", "!", true)]
    [InlineData("[[:lower:]]", "a", true)]
    [InlineData("[[:lower:]]", "A", false)]
    [InlineData("[[:print:]]", " ", true)]
    [InlineData("[[:print:]]", "\t", false)]
    [InlineData("[[:punct:]]", "!", true)]
    [InlineData("[[:punct:]]", "a", false)]
    [InlineData("[[:space:]]", "\r", true)]
    [InlineData("[[:upper:]]", "Z", true)]
    [InlineData("[[:upper:]]", "z", false)]
    [InlineData("[[:xdigit:]]", "F", true)]
    [InlineData("[[:xdigit:]]", "g", false)]
    [InlineData("a/[!x]/c", "a/b/c", true)]
    [InlineData("a/[!x]c", "a/b/c", false)]
    public void GitIgnore_matches_bracket_and_posix_classes(string pattern, string path, bool expected)
    {
        (GitIgnoreFile.Parse(pattern).MatchPath(path) is true).Should().Be(expected);
    }

    [Theory]
    [InlineData("FILE", "file", true, true)]
    [InlineData("FILE", "file", false, false)]
    [InlineData("/A/FILE", "a/file", true, true)]
    [InlineData("[a-z]", "Z", true, true)]
    [InlineData("[A-Z]", "z", true, true)]
    [InlineData("[A]", "a", true, false)]
    [InlineData("[a]", "A", true, true)]
    [InlineData("\\A", "A", true, false)]
    [InlineData("[A-z]", "_", true, true)]
    [InlineData("[!a]", "A", true, false)]
    [InlineData("[[:upper:]]", "a", true, true)]
    [InlineData("[[:lower:]]", "A", true, true)]
    [InlineData("é", "é", false, true)]
    [InlineData("É", "é", true, false)]
    [InlineData("?", "é", false, false)]
    [InlineData("??", "é", false, true)]
    [InlineData("????", "😀", false, true)]
    public void GitIgnore_matches_ascii_case_folding_and_utf8_bytes(
        string pattern, string path, bool ignoreCase, bool expected)
    {
        (GitIgnoreFile.Parse(pattern).MatchPath(path, ignoreCase: ignoreCase) is true).Should().Be(expected);
    }

    [Theory]
    [InlineData("*.txt\n!keep.txt", "keep.txt", false, false, false)]
    [InlineData("!keep.txt\n*.txt", "keep.txt", false, true, true)]
    [InlineData("!keep.txt", "keep.txt", false, false, false)]
    [InlineData("cache/", "cache/file", false, null, true)]
    [InlineData("cache/", "cache", false, null, false)]
    [InlineData("cache", "cache/file", false, null, true)]
    [InlineData("cache/\n!cache/keep.txt", "cache/keep.txt", false, false, true)]
    [InlineData("cache/\n!cache/", "cache/file", false, null, false)]
    [InlineData("cache/\n!cache/\n*.txt", "cache/file.txt", false, true, true)]
    [InlineData("cache/*\n!cache/keep.txt", "cache/keep.txt", false, false, false)]
    [InlineData("cache/*\n!cache/nested/keep.txt", "cache/nested/keep.txt", false, false, true)]
    [InlineData("a/**\n!a/b/\n!a/b/c", "a/b/c", false, false, false)]
    [InlineData("a/**\n!a/b/c", "a/b/c", false, false, true)]
    [InlineData("a/**\n!a/**/", "a/b/c", false, true, true)]
    [InlineData("a/**\n!a/**/\n!a/b/c", "a/b/c", false, false, false)]
    [InlineData("*.log\n!keep.log", "a.log/keep.log", false, false, true)]
    [InlineData("/root/", "other/root/file", false, null, false)]
    [InlineData("/root/", "root/other/file", false, null, true)]
    [InlineData("folder/\n!folder", "folder", true, false, false)]
    [InlineData("folder/", "folder/", false, true, true)]
    [InlineData("a/**/", "a/file", false, null, false)]
    [InlineData("a/**/", "a/b/file", false, null, true)]
    public void GitIgnore_applies_last_rule_and_excluded_parent_semantics(
        string content, string path, bool directory, bool? targetDecision, bool ignored)
    {
        var file = GitIgnoreFile.Parse(content);
        file.MatchPath(path, directory).Should().Be(targetDecision);
        file.IsIgnored(path, directory).Should().Be(ignored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/root")]
    [InlineData("\\root")]
    [InlineData("C:/root")]
    [InlineData("C:\\root")]
    [InlineData("//server/share")]
    [InlineData("a\0b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("./a")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("a/..")]
    [InlineData("a//b")]
    [InlineData("a//")]
    public void GitIgnore_rejects_invalid_relative_paths(string path)
    {
        var file = GitIgnoreFile.Parse("");
        var match = () => file.MatchPath(path);
        var ignore = () => file.IsIgnored(path);
        match.Should().Throw<ArgumentException>();
        ignore.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void GitIgnore_rejects_null_inputs()
    {
        var parse = () => GitIgnoreFile.Parse(null!);
        var match = () => GitIgnoreFile.Parse("").MatchPath(null!);
        var ignore = () => GitIgnoreFile.Parse("").IsIgnored(null!);
        parse.Should().Throw<ArgumentNullException>();
        match.Should().Throw<ArgumentNullException>();
        ignore.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GitIgnore_applies_case_folding_to_ancestors_and_directory_negations()
    {
        var file = GitIgnoreFile.Parse("CACHE/\n!cache/keep.txt");
        file.MatchPath("cache/keep.txt", ignoreCase: true).Should().BeFalse();
        file.IsIgnored("cache/keep.txt", ignoreCase: true).Should().BeTrue();
        file.IsIgnored("cache/keep.txt").Should().BeFalse();

        GitIgnoreFile.Parse("CACHE/\n!cache/\n!cache/keep.txt")
            .IsIgnored("cache/keep.txt", ignoreCase: true).Should().BeFalse();
    }

    [Fact]
    public void GitIgnore_handles_adversarial_wildcards_without_backtracking_or_recursion()
    {
        var pattern = string.Concat(Enumerable.Repeat("*a", 300)) + "b";
        var path = new string('a', 2_000);
        GitIgnoreFile.Parse(pattern).MatchPath(path).Should().BeNull();

        var directoryPattern = string.Concat(Enumerable.Repeat("**/a/", 80)) + "missing";
        var directoryPath = string.Concat(Enumerable.Repeat("a/", 160)) + "file";
        GitIgnoreFile.Parse(directoryPattern).MatchPath(directoryPath).Should().BeNull();
    }

    [Fact]
    public async Task GitIgnore_is_safe_for_concurrent_content_cache_reuse()
    {
        var file = GitIgnoreFile.Parse("cache/\n*.log\n!keep.log");

        await Task.WhenAll(
            Enumerable.Range(0, 32).Select(
                _ => Task.Run(
                    () =>
                    {
                        file.MatchPath("a/file.log").Should().BeTrue();
                        file.MatchPath("cache/keep.log").Should().BeFalse();
                        file.IsIgnored("cache/keep.log").Should().BeTrue();
                        file.IsIgnored("a/keep.log").Should().BeFalse();
                    })));
    }
}
