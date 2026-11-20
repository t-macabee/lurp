using System.Text;
using Lurp.Storage;
using Microsoft.CodeAnalysis.Text;

namespace Lurp.Tests;

public sealed class LineBreaksTests
{
    public static TheoryData<string, int[]> Cases() => new()
    {
        { "", [0] },
        { "a", [0] },
        { "a\n", [0, 2] },
        { "\r\n", [0, 2] },
        { "a\r", [0, 2] },
        { "a\n\r", [0, 2, 3] },
        { "é\nx", [0, 3] },
        { "a\rb\nc\r\nd\u0085e\u2028f\u2029g", [0, 2, 4, 7, 10, 14, 18] }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ComputeLineStarts_MatchesExpectedAndRoslynSourceText(string text, int[] expectedByteStarts)
    {
        var utf8Bytes = Encoding.UTF8.GetBytes(text);
        var actual = LineBreaks.ComputeLineStarts(utf8Bytes);

        Assert.Equal(expectedByteStarts, actual);

        var roslynLines = SourceText.From(text).Lines;
        var roslynByteStarts = roslynLines
            .Select(line => Encoding.UTF8.GetByteCount(text.AsSpan(0, line.Start)))
            .ToArray();

        Assert.Equal(roslynByteStarts, actual);
    }
}
