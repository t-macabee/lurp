// Purpose: token-window snippet formatter shared by source search FTS and fragment fallback paths.
// Owns: token segmentation (runs of letters/digits), first-match windowing, and <mark>/… formatting.
// Must not contain: SQLite or Roslyn dependencies.

using System.Text;

namespace Lurp.Storage;

internal static class SnippetWindowBuilder
{
    private const char HighlightStartMarker = '\u0002';
    private const char HighlightEndMarker = '\u0003';

    internal static string BuildFromHighlighted(string content, string highlighted, int snippetTokens)
    {
        var contentIndex = 0;
        var matchStart = -1;

        foreach (var c in highlighted)
        {
            if (contentIndex < content.Length && c == content[contentIndex])
            {
                contentIndex++;
                continue;
            }

            if (c == HighlightStartMarker && matchStart < 0)
            {
                matchStart = contentIndex;
            }
            else if (c == HighlightEndMarker && matchStart >= 0)
            {
                // FTS inserts the close marker before any identical source characters.
                // Alignment can consume those characters before it detects the extra marker.
                var matchEnd = contentIndex;
                while (matchEnd > matchStart && content[matchEnd - 1] == HighlightEndMarker)
                    matchEnd--;

                return BuildWindow(content, matchStart, matchEnd - matchStart, snippetTokens);
            }
        }

        return BuildWindow(content, 0, 0, snippetTokens);
    }

    internal static string BuildFromPlainText(string text, string query, int snippetTokens)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return BuildWindow(text, 0, 0, snippetTokens);

        return BuildWindow(text, idx, query.Length, snippetTokens);
    }

    internal static string BuildWindow(string text, int matchStart, int matchLength, int snippetTokens)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        snippetTokens = Math.Max(1, snippetTokens);
        matchStart = Math.Clamp(matchStart, 0, text.Length);
        matchLength = Math.Clamp(matchLength, 0, text.Length - matchStart);
        var matchEnd = matchStart + matchLength;

        var tokens = FindTokens(text);
        if (tokens.Count == 0)
            return WrapPlainOrMatch(text, matchStart, matchEnd);

        var firstMatchToken = -1;
        var lastMatchToken = -1;
        if (matchLength > 0)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                var (start, end) = tokens[i];
                if (start < matchEnd && end > matchStart)
                {
                    if (firstMatchToken < 0)
                        firstMatchToken = i;
                    lastMatchToken = i;
                }
            }
        }

        if (firstMatchToken < 0)
            return WrapPlainOrMatch(text, matchStart, matchEnd);

        var m = lastMatchToken - firstMatchToken + 1;
        var n = Math.Max(snippetTokens, m);
        var before = Math.Min(firstMatchToken, (n - m) / 2);
        var windowStart = firstMatchToken - before;
        var windowEnd = Math.Min(tokens.Count - 1, windowStart + n - 1);
        windowStart = Math.Max(0, windowEnd - n + 1);

        var charStart = Math.Min(tokens[windowStart].Start, matchStart);
        var charEnd = Math.Max(tokens[windowEnd].End, matchEnd);

        var hasPrefix = windowStart > 0;
        var hasSuffix = windowEnd < tokens.Count - 1;

        var result = new StringBuilder((charEnd - charStart) + 16);
        if (hasPrefix)
            result.Append('…');
        result.Append(text, charStart, matchStart - charStart);
        result.Append("<mark>");
        result.Append(text, matchStart, matchLength);
        result.Append("</mark>");
        result.Append(text, matchEnd, charEnd - matchEnd);
        if (hasSuffix)
            result.Append('…');
        return result.ToString();
    }

    private static string WrapPlainOrMatch(string text, int matchStart, int matchEnd)
    {
        if (matchEnd > matchStart)
            return $"<mark>{text[matchStart..matchEnd]}</mark>";
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    private static List<(int Start, int End)> FindTokens(string text)
    {
        var tokens = new List<(int Start, int End)>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            i++;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
                i++;
            tokens.Add((start, i));
        }
        return tokens;
    }
}
