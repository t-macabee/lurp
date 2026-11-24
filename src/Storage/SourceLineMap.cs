using System.Text;
using System.Text.Json;

namespace Lurp.Storage;

/// <summary>
///     The single owner of byte-span-to-line mapping. Absent data (NULL
///     <c>line_starts</c>, NULL span columns) means "no line mapping" and returns
///     <c>null</c>; present data that does not parse, or a span outside the
///     content, is a storage or extractor bug and throws.
/// </summary>
public static class SourceLineMap
{
    public static int[]? ParseLineStarts(string? json, string documentVersionId)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        int[]? lineStarts;
        try
        {
            lineStarts = JsonSerializer.Deserialize<int[]>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse line_starts for document version '{documentVersionId}'.", ex);
        }

        if (lineStarts is not { Length: > 0 })
            throw new InvalidOperationException(
                $"line_starts for document version '{documentVersionId}' is an empty array.");

        return lineStarts;
    }

    public static int FindLineIndex(int[] lineStarts, int byteOffset)
    {
        int lo = 0, hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= byteOffset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    public static int FindEndLineIndex(int[] lineStarts, int fullStart, int fullEnd) =>
        FindLineIndex(lineStarts, fullEnd > fullStart ? fullEnd - 1 : fullStart);

    public static int Utf8Column(byte[] content, int lineStart, int offset)
    {
        var safeOffset = Math.Clamp(offset, lineStart, content.Length);
        return Encoding.UTF8.GetCharCount(content, lineStart, safeOffset - lineStart);
    }

    public static string SliceUtf8(byte[] content, int start, int end, string documentVersionId)
    {
        if (start < 0 || end > content.Length || start > end)
            throw new InvalidOperationException(
                $"Span [{start}, {end}) is outside the {content.Length} bytes of document version '{documentVersionId}'.");
        return end == start ? string.Empty : Encoding.UTF8.GetString(content, start, end - start);
    }

    public static string ExtractLineText(byte[] content, int[] lineStarts, int lineIndex, string documentVersionId)
    {
        if (lineIndex < 0 || lineIndex >= lineStarts.Length)
            throw new InvalidOperationException(
                $"Line index {lineIndex} is outside the {lineStarts.Length} lines of document version '{documentVersionId}'.");

        var lineStart = lineStarts[lineIndex];
        var hasNextLine = lineIndex + 1 < lineStarts.Length;
        var lineEnd = hasNextLine ? lineStarts[lineIndex + 1] : content.Length;
        if (lineStart < 0 || lineEnd > content.Length || lineStart > lineEnd)
            throw new InvalidOperationException(
                $"Line span [{lineStart}, {lineEnd}) is outside the {content.Length} bytes of document version '{documentVersionId}'.");

        if (hasNextLine)
            lineEnd -= LineBreaks.TrailingLineBreakByteLength(content.AsSpan(lineStart, lineEnd - lineStart));

        return lineEnd <= lineStart ? string.Empty : Encoding.UTF8.GetString(content, lineStart, lineEnd - lineStart);
    }

    /// <summary>Byte bounds of whole lines <paramref name="firstLineIndex" /> to <paramref name="lastLineIndex" /> (0-based), widened by <paramref name="contextLines" /> on each side and clamped to the document.</summary>
    public static (int ByteStart, int ByteEnd, int FirstLineIndex, int LastLineIndex) ExpandLineWindow(
        int[] lineStarts, int firstLineIndex, int lastLineIndex, int contextLines, int contentLength)
    {
        var first = Math.Max(0, firstLineIndex - contextLines);
        var last = Math.Min(lineStarts.Length - 1, lastLineIndex + contextLines);
        var byteEnd = last + 1 < lineStarts.Length ? lineStarts[last + 1] : contentLength;
        return (lineStarts[first], byteEnd, first, last);
    }

    public static DeclarationLocation? MapDeclaration(
        string documentPath,
        int? fullStart,
        int? fullEnd,
        int[]? lineStarts,
        byte[]? content,
        bool isGenerated,
        string documentVersionId,
        string symbolId)
    {
        if (fullStart is null || fullEnd is null || lineStarts is not { Length: > 0 } || content is null)
            return null;

        if (fullStart < 0 || fullEnd < fullStart || fullEnd > content.Length)
            throw new InvalidOperationException(
                $"Declaration span [{fullStart}, {fullEnd}) for symbol '{symbolId}' is outside document version '{documentVersionId}' ({content.Length} bytes).");

        var startLineIndex = FindLineIndex(lineStarts, fullStart.Value);
        var endLineIndex = FindEndLineIndex(lineStarts, fullStart.Value, fullEnd.Value);
        var endOffset = fullEnd.Value;
        if (endLineIndex + 1 < lineStarts.Length && endOffset == lineStarts[endLineIndex + 1])
            endOffset -= LineBreaks.TrailingLineBreakByteLength(content.AsSpan(lineStarts[endLineIndex], endOffset - lineStarts[endLineIndex]));

        return new DeclarationLocation(
            documentPath,
            LineNumbers.ToOneBased(startLineIndex),
            Utf8Column(content, lineStarts[startLineIndex], fullStart.Value),
            LineNumbers.ToOneBased(endLineIndex),
            Utf8Column(content, lineStarts[endLineIndex], endOffset),
            isGenerated);
    }
}
