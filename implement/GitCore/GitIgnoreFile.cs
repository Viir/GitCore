using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace GitCore;

/// <summary>
/// An immutable, parsed .gitignore file. Paths are relative to the directory containing that file.
/// Matching uses UTF-8 bytes and Git's ASCII case folding and POSIX character classes.
/// No filesystem access is performed.
/// </summary>
public sealed class GitIgnoreFile
{
    /// <summary>
    /// An immutable rule, including its original source and the information used for matching.
    /// </summary>
    public sealed class Rule
    {
        private readonly Token[] tokens;

        /// <summary>The one-based line number in the source file.</summary>
        public int SourceLineNumber { get; }

        /// <summary>The source line, without its line ending or the file's initial BOM.</summary>
        public string SourceLine { get; }

        /// <summary>The pattern without negation, leading anchoring slash or directory-only suffix. Escapes are preserved.</summary>
        public string Pattern { get; }

        /// <summary>Whether this rule re-includes its matches.</summary>
        public bool IsNegated { get; }

        /// <summary>Whether this rule matches directories only.</summary>
        public bool IsDirectoryOnly { get; }

        /// <summary>Whether the source pattern has a leading anchoring slash.</summary>
        public bool HasLeadingSlash { get; }

        /// <summary>Whether the pattern is relative to the .gitignore directory rather than matching basenames at any depth.</summary>
        public bool IsRootAnchored { get; }

        /// <summary>Whether the pattern matches only the target's basename, at any depth.</summary>
        public bool MatchesBasename => !IsRootAnchored;

        internal Rule(
            int sourceLineNumber,
            string sourceLine,
            string pattern,
            bool isNegated,
            bool isDirectoryOnly,
            bool hasLeadingSlash)
        {
            SourceLineNumber = sourceLineNumber;
            SourceLine = sourceLine;
            Pattern = pattern;
            IsNegated = isNegated;
            IsDirectoryOnly = isDirectoryOnly;
            HasLeadingSlash = hasLeadingSlash;
            IsRootAnchored = hasLeadingSlash || pattern.Contains('/');
            tokens = Compile(Encoding.UTF8.GetBytes(pattern));
        }

        internal bool Matches(ReadOnlySpan<byte> path, bool isDirectory, bool ignoreCase)
        {
            if (IsDirectoryOnly && !isDirectory)
                return false;

            if (MatchesBasename)
                path = path[(path.LastIndexOf((byte)'/') + 1)..];

            return Match(tokens, path, ignoreCase);
        }
    }

    /// <summary>The exact original content, suitable for caller-owned, content-keyed caching.</summary>
    public string Content { get; }

    /// <summary>Rules in source order, exposed through an immutable collection.</summary>
    public IReadOnlyList<Rule> Rules { get; }

    private GitIgnoreFile(string content, List<Rule> rules)
    {
        Content = content;
        Rules = new ReadOnlyCollection<Rule>(rules.ToArray());
    }

    /// <summary>
    /// Parses Git comments, negations, escapes, trailing spaces, CRLF and an initial UTF-8 BOM.
    /// </summary>
    public static GitIgnoreFile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var rules = new List<Rule>();
        var lines = content.Split('\n');

        for (var lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
        {
            var source = lines[lineIndex];

            if (lineIndex is 0 && source.StartsWith('\uFEFF'))
                source = source[1..];

            if (source.EndsWith('\r'))
                source = source[..^1];

            var nulIndex = source.IndexOf('\0');
            var pattern = TrimUnescapedTrailingSpaces(nulIndex < 0 ? source : source[..nulIndex]);

            if (pattern.Length is 0 || pattern[0] is '#')
                continue;

            var isNegated = pattern[0] is '!';

            if (isNegated)
                pattern = pattern[1..];

            var isDirectoryOnly = pattern.EndsWith('/');

            if (isDirectoryOnly)
                pattern = pattern[..^1];

            var hasLeadingSlash = pattern.StartsWith('/');

            if (hasLeadingSlash)
                pattern = pattern[1..];

            if (pattern.Length is 0)
                continue;

            rules.Add(
                new Rule(lineIndex + 1, source, pattern, isNegated, isDirectoryOnly, hasLeadingSlash));
        }

        return new GitIgnoreFile(content, rules);
    }

    /// <summary>
    /// Returns the last matching rule's decision: true for ignored, false for negated, or null for no match.
    /// Only the target is matched; ancestors are not evaluated. A trailing slash implies a directory.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, rooted, contains NUL, or has empty, "." or ".." components.</exception>
    public bool? MatchPath(string relativePath, bool isDirectory = false, bool ignoreCase = false)
    {
        var path = ValidateAndEncodePath(relativePath, ref isDirectory);
        return MatchRules(path, isDirectory, ignoreCase);
    }

    /// <summary>
    /// Evaluates the target and its ancestors. A negation cannot re-include a target beneath an excluded directory.
    /// A trailing slash implies a directory.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, rooted, contains NUL, or has empty, "." or ".." components.</exception>
    public bool IsIgnored(string relativePath, bool isDirectory = false, bool ignoreCase = false)
    {
        var path = ValidateAndEncodePath(relativePath, ref isDirectory);

        for (var index = 0; index < path.Length; ++index)
        {
            if (path[index] is (byte)'/' && MatchRules(path.AsSpan(0, index), true, ignoreCase) is true)
                return true;
        }

        return MatchRules(path, isDirectory, ignoreCase) is true;
    }

    private bool? MatchRules(ReadOnlySpan<byte> path, bool isDirectory, bool ignoreCase)
    {
        for (var index = Rules.Count - 1; index >= 0; --index)
        {
            if (Rules[index].Matches(path, isDirectory, ignoreCase))
                return !Rules[index].IsNegated;
        }

        return null;
    }

    private static byte[] ValidateAndEncodePath(string path, ref bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length is 0 ||
            path[0] is '/' or '\\' ||
            (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] is ':' && path[2] is '/' or '\\') ||
            path.Contains('\0'))
        {
            throw new ArgumentException("Expected a nonempty, slash-separated relative Git path.", nameof(path));
        }

        if (path.EndsWith('/'))
        {
            isDirectory = true;
            path = path[..^1];
        }

        foreach (var component in path.Split('/'))
        {
            if (component is "" or "." or "..")
                throw new ArgumentException("Git paths cannot contain empty, '.' or '..' components.", nameof(path));
        }

        return Encoding.UTF8.GetBytes(path);
    }

    private static string TrimUnescapedTrailingSpaces(string pattern)
    {
        var length = pattern.Length;

        while (length > 0 && pattern[length - 1] is ' ')
        {
            var backslashes = 0;

            for (var index = length - 2; index >= 0 && pattern[index] is '\\'; --index)
                ++backslashes;

            if (backslashes % 2 is 1)
                break;

            --length;
        }

        return pattern[..length];
    }

    private enum TokenKind
    {
        Literal,
        EscapedLiteral,
        Any,
        Star,
        GlobStar,
        GlobDirectories,
        CharacterClass,
        Invalid
    }

    private readonly record struct Token(
        TokenKind Kind,
        byte Literal = 0,
        CharacterClass? CharacterClass = null);

    private static Token[] Compile(byte[] pattern)
    {
        var tokens = new List<Token>();

        for (var index = 0; index < pattern.Length; ++index)
        {
            switch (pattern[index])
            {
                case (byte)'\\':
                    if (++index >= pattern.Length)
                        return [new Token(TokenKind.Invalid)];

                    tokens.Add(new Token(TokenKind.EscapedLiteral, pattern[index]));
                    break;

                case (byte)'?':
                    tokens.Add(new Token(TokenKind.Any));
                    break;

                case (byte)'*':
                    var start = index;

                    while (index + 1 < pattern.Length && pattern[index + 1] is (byte)'*')
                        ++index;

                    var atStartBoundary = start is 0 || pattern[start - 1] is (byte)'/';
                    var followedBySlash = index + 1 < pattern.Length && pattern[index + 1] is (byte)'/';
                    var followedByEscapedSlash =
                        index + 2 < pattern.Length &&
                        pattern[index + 1] is (byte)'\\' && pattern[index + 2] is (byte)'/';
                    var atEndBoundary = index + 1 == pattern.Length || followedBySlash || followedByEscapedSlash;

                    if (index > start && atStartBoundary && atEndBoundary)
                    {
                        if (followedBySlash)
                        {
                            tokens.Add(new Token(TokenKind.GlobDirectories));
                            ++index;
                        }
                        else
                        {
                            tokens.Add(new Token(TokenKind.GlobStar));
                        }
                    }
                    else
                    {
                        tokens.Add(new Token(TokenKind.Star));
                    }

                    break;

                case (byte)'[':
                    var characterClass = ParseCharacterClass(pattern, ref index);

                    if (characterClass is null)
                        return [new Token(TokenKind.Invalid)];

                    tokens.Add(new Token(TokenKind.CharacterClass, CharacterClass: characterClass));
                    break;

                default:
                    tokens.Add(new Token(TokenKind.Literal, pattern[index]));
                    break;
            }
        }

        return tokens.ToArray();
    }

    private static bool Match(Token[] tokens, ReadOnlySpan<byte> path, bool ignoreCase)
    {
        // Each row represents reachable path offsets after consuming one token. This bounds
        // wildcard matching to O(pattern length * path length), with no recursion or backtracking.
        var previous = new bool[path.Length + 1];
        var next = new bool[path.Length + 1];
        previous[0] = true;

        foreach (var token in tokens)
        {
            Array.Clear(next);
            var earlierReachable = false;
            var anyReachable = false;

            for (var offset = 0; offset <= path.Length; ++offset)
            {
                next[offset] =
                    token.Kind switch
                    {
                        TokenKind.Star =>
                            previous[offset] || (offset > 0 && path[offset - 1] is not (byte)'/' && next[offset - 1]),
                        TokenKind.GlobStar =>
                            previous[offset] || (offset > 0 && next[offset - 1]),
                        TokenKind.GlobDirectories =>
                            previous[offset] || (offset > 0 && path[offset - 1] is (byte)'/' && earlierReachable),
                        TokenKind.Literal =>
                            offset > 0 && previous[offset - 1] && Equal(token.Literal, path[offset - 1], ignoreCase),
                        TokenKind.EscapedLiteral =>
                            // Git folds the input byte, but not an escaped pattern byte.
                            offset > 0 && previous[offset - 1] &&
                            token.Literal == (ignoreCase ? FoldCase(path[offset - 1]) : path[offset - 1]),
                        TokenKind.Any =>
                            offset > 0 && previous[offset - 1] && path[offset - 1] is not (byte)'/',
                        TokenKind.CharacterClass =>
                            offset > 0 && previous[offset - 1] && path[offset - 1] is not (byte)'/' &&
                            token.CharacterClass!.Matches(path[offset - 1], ignoreCase),
                        _ => false
                    };

                earlierReachable |= previous[offset];
                anyReachable |= next[offset];
            }

            if (!anyReachable)
                return false;

            (previous, next) = (next, previous);
        }

        return previous[path.Length];
    }

    private static bool Equal(byte left, byte right, bool ignoreCase) =>
        left == right || (ignoreCase && FoldCase(left) == FoldCase(right));

    private static byte FoldCase(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' ? (byte)(value + ('a' - 'A')) : value;

    private enum PosixClass
    {
        None,
        Alnum,
        Alpha,
        Blank,
        Cntrl,
        Digit,
        Graph,
        Lower,
        Print,
        Punct,
        Space,
        Upper,
        Xdigit
    }

    private readonly record struct ClassElement(
        byte First, byte Last, PosixClass Posix = PosixClass.None, bool IsRange = false)
    {
        public bool Matches(byte value, bool ignoreCase)
        {
            if (ignoreCase)
                value = FoldCase(value);

            if (Posix is not PosixClass.None)
            {
                var lower = value is >= (byte)'a' and <= (byte)'z';
                var upper = value is >= (byte)'A' and <= (byte)'Z';
                var digit = value is >= (byte)'0' and <= (byte)'9';

                return Posix switch
                {
                    PosixClass.Alnum => lower || upper || digit,
                    PosixClass.Alpha => lower || upper,
                    PosixClass.Blank => value is (byte)' ' or (byte)'\t',
                    PosixClass.Cntrl => value < 32 || value is 127,
                    PosixClass.Digit => digit,
                    PosixClass.Graph => value is >= 33 and <= 126,
                    PosixClass.Lower => lower || (ignoreCase && upper),
                    PosixClass.Print => value is >= 32 and <= 126,
                    PosixClass.Punct => value is >= 33 and <= 126 && !(lower || upper || digit),
                    PosixClass.Space => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 11 or 12,
                    PosixClass.Upper => upper || (ignoreCase && lower),
                    PosixClass.Xdigit => digit || FoldCase(value) is >= (byte)'a' and <= (byte)'f',
                    _ => false
                };
            }

            if (!IsRange)
                return value == First;

            if (value >= First && value <= Last)
                return true;

            if (!ignoreCase)
                return false;

            var folded = FoldCase(value);
            var capitalized =
                folded is >= (byte)'a' and <= (byte)'z' ? (byte)(folded - ('a' - 'A')) : folded;

            return (folded >= First && folded <= Last) || (capitalized >= First && capitalized <= Last);
        }
    }

    private sealed class CharacterClass(bool negated, ClassElement[] elements)
    {
        public bool Matches(byte value, bool ignoreCase)
        {
            foreach (var element in elements)
            {
                if (element.Matches(value, ignoreCase))
                    return !negated;
            }

            return negated;
        }
    }

    private static CharacterClass? ParseCharacterClass(byte[] pattern, ref int index)
    {
        var cursor = index + 1;
        var negated = cursor < pattern.Length && pattern[cursor] is (byte)'!' or (byte)'^';

        if (negated)
            ++cursor;

        var elements = new List<ClassElement>();

        while (cursor < pattern.Length)
        {
            if (pattern[cursor] is (byte)']' && elements.Count > 0)
            {
                index = cursor;
                return new CharacterClass(negated, elements.ToArray());
            }

            if (!ReadClassElement(pattern, ref cursor, out var element))
                return null;

            if (element.Posix is PosixClass.None &&
                cursor + 1 < pattern.Length && pattern[cursor] is (byte)'-' && pattern[cursor + 1] is not (byte)']')
            {
                ++cursor;

                var last = pattern[cursor++];

                if (last is (byte)'\\')
                {
                    if (cursor >= pattern.Length)
                        return null;

                    last = pattern[cursor++];
                }

                elements.Add(element);
                element = new ClassElement(element.First, last, IsRange: true);
            }

            elements.Add(element);
        }

        return null;
    }

    private static bool ReadClassElement(byte[] pattern, ref int cursor, out ClassElement element)
    {
        element = default;

        if (cursor >= pattern.Length)
            return false;

        var value = pattern[cursor++];

        if (value is (byte)'\\')
        {
            if (cursor >= pattern.Length)
                return false;

            value = pattern[cursor++];
        }
        else if (value is (byte)'[' && cursor < pattern.Length && pattern[cursor] is (byte)':')
        {
            var literalCursor = cursor;
            var start = ++cursor;

            while (cursor < pattern.Length && pattern[cursor] is not (byte)']')
            {
                ++cursor;
            }

            if (cursor >= pattern.Length)
                return false;

            if (cursor == start || pattern[cursor - 1] is not (byte)':')
            {
                cursor = literalCursor;
                element = new ClassElement(value, value);
                return true;
            }

            var name = Encoding.ASCII.GetString(pattern, start, cursor - start - 1);
            var posix = name switch
            {
                "alnum" => PosixClass.Alnum,
                "alpha" => PosixClass.Alpha,
                "blank" => PosixClass.Blank,
                "cntrl" => PosixClass.Cntrl,
                "digit" => PosixClass.Digit,
                "graph" => PosixClass.Graph,
                "lower" => PosixClass.Lower,
                "print" => PosixClass.Print,
                "punct" => PosixClass.Punct,
                "space" => PosixClass.Space,
                "upper" => PosixClass.Upper,
                "xdigit" => PosixClass.Xdigit,
                _ => PosixClass.None
            };

            if (posix is PosixClass.None)
                return false;

            ++cursor;
            element = new ClassElement(0, 0, posix);
            return true;
        }

        element = new ClassElement(value, value);
        return true;
    }
}
