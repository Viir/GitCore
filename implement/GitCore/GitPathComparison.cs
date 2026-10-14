using System;
using System.Collections.Generic;

namespace GitCore;

internal static class GitPathComparison
{
    public static IEqualityComparer<string> IgnoreCaseComparer { get; } = new AsciiIgnoreCaseComparer();

    public static bool StartsWith(string path, string prefix, bool ignoreCase)
    {
        if (!ignoreCase)
            return path.StartsWith(prefix, StringComparison.Ordinal);

        if (path.Length < prefix.Length)
            return false;

        for (var i = 0; i < prefix.Length; ++i)
        {
            if (Fold(path[i]) != Fold(prefix[i]))
                return false;
        }

        return true;
    }

    private static char Fold(char character)
    {
        return character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character;
    }

    private sealed class AsciiIgnoreCaseComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right)
        {
            if (ReferenceEquals(left, right))
                return true;

            return left is not null && right is not null &&
                left.Length == right.Length && StartsWith(left, right, ignoreCase: true);
        }

        public int GetHashCode(string value)
        {
            var hash = new HashCode();

            foreach (var character in value)
                hash.Add(Fold(character));

            return hash.ToHashCode();
        }
    }
}
