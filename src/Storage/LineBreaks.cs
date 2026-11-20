using System.Text.Json;

namespace Lurp.Storage;

/// <summary>
///     Computes UTF-8 byte offsets of line starts using the Roslyn / C# line-break rule:
///     CR, LF, CRLF (single break), NEL (U+0085), LS (U+2028), and PS (U+2029).
///     A line break at the end of the content starts a trailing empty line (D186.1).
/// </summary>
public static class LineBreaks
{
    public static int[] ComputeLineStarts(ReadOnlySpan<byte> utf8Bytes)
    {
        var offsets = new List<int> { 0 };
        for (var i = 0; i < utf8Bytes.Length; i++)
        {
            var b = utf8Bytes[i];
            if (b == (byte)'\r')
            {
                if (i + 1 < utf8Bytes.Length && utf8Bytes[i + 1] == (byte)'\n')
                    i++;
                offsets.Add(i + 1);
            }
            else if (b == (byte)'\n')
            {
                offsets.Add(i + 1);
            }
            else if (b == 0xC2 && i + 1 < utf8Bytes.Length && utf8Bytes[i + 1] == 0x85)
            {
                i++;
                offsets.Add(i + 1);
            }
            else if (b == 0xE2 && i + 2 < utf8Bytes.Length && utf8Bytes[i + 1] == 0x80 &&
                     (utf8Bytes[i + 2] == 0xA8 || utf8Bytes[i + 2] == 0xA9))
            {
                i += 2;
                offsets.Add(i + 1);
            }
        }

        return [.. offsets];
    }

    public static string SerializeLineStarts(int[] lineStarts)
    {
        return JsonSerializer.Serialize(lineStarts);
    }

    public static string ComputeLineStartsJson(ReadOnlySpan<byte> utf8Bytes)
    {
        return SerializeLineStarts(ComputeLineStarts(utf8Bytes));
    }

    internal static int TrailingLineBreakByteLength(ReadOnlySpan<byte> lineBytes)
    {
        var len = lineBytes.Length;
        if (len >= 2 && lineBytes[len - 2] == (byte)'\r' && lineBytes[len - 1] == (byte)'\n')
            return 2;
        if (len >= 1 && (lineBytes[len - 1] == (byte)'\r' || lineBytes[len - 1] == (byte)'\n'))
            return 1;
        if (len >= 2 && lineBytes[len - 2] == 0xC2 && lineBytes[len - 1] == 0x85)
            return 2;
        if (len >= 3 && lineBytes[len - 3] == 0xE2 && lineBytes[len - 2] == 0x80 &&
            (lineBytes[len - 1] == 0xA8 || lineBytes[len - 1] == 0xA9))
            return 3;
        return 0;
    }
}
