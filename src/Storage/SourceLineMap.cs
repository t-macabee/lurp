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

    public static int Utf8Column(byte[] content, int lineStart, int offset)
    {
        var safeOffset = Math.Clamp(offset, lineStart, content.Length);
        return Encoding.UTF8.GetCharCount(content, lineStart, safeOffset - lineStart);
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
        var endLineIndex = FindLineIndex(lineStarts, fullEnd.Value);
        return new DeclarationLocation(
            documentPath,
            LineNumbers.ToOneBased(startLineIndex),
            Utf8Column(content, lineStarts[startLineIndex], fullStart.Value),
            LineNumbers.ToOneBased(endLineIndex),
            Utf8Column(content, lineStarts[endLineIndex], fullEnd.Value),
            isGenerated);
    }
}
