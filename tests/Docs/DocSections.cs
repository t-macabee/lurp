using System.Text.RegularExpressions;

namespace Lurp.Tests.Docs;

internal static partial class DocSections
{
    [GeneratedRegex(@"<!--\s*(/?)generated:([a-z0-9:-]+)\s*-->")]
    private static partial Regex MarkerPattern();

    internal static IReadOnlyList<string> FindKeys(string text)
    {
        var keys = new List<string>();
        foreach (Match match in MarkerPattern().Matches(text))
        {
            if (match.Groups[1].Value.Length == 0)
                keys.Add(match.Groups[2].Value);
        }

        return keys;
    }

    internal static string? Extract(string text, string key)
    {
        var located = Locate(text, key);
        if (located is null)
            return null;
        return text[(located.Value.Begin.Index + located.Value.Begin.Length)..located.Value.End.Index];
    }

    internal static string Replace(string text, string key, string content)
    {
        var located = Locate(text, key)
            ?? throw new InvalidOperationException($"No generated marker for '{key}'.");
        var begin = located.Begin;
        var end = located.End;
        return text[..(begin.Index + begin.Length)] + content + text[end.Index..];
    }

    private static (Match Begin, Match End)? Locate(string text, string key)
    {
        Match? begin = null;
        foreach (Match match in MarkerPattern().Matches(text))
        {
            if (match.Groups[1].Value.Length > 0
                || !string.Equals(match.Groups[2].Value, key, StringComparison.Ordinal))
                continue;
            if (begin is not null)
                throw new InvalidOperationException($"Generated marker '{key}' appears more than once.");
            begin = match;
        }

        if (begin is null)
            return null;

        foreach (Match match in MarkerPattern().Matches(text, begin.Index + begin.Length))
        {
            if (match.Groups[1].Value.Length == 0
                || !string.Equals(match.Groups[2].Value, key, StringComparison.Ordinal))
                continue;
            return (begin, match);
        }

        throw new InvalidOperationException($"Generated marker '{key}' has no end marker.");
    }
}
