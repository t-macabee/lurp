using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     D108 (5): README keeps its mode table, and its mode names equal the mode registry without
///     <c>serve</c> (README names <c>serve</c> in the MCP paragraph).
/// </summary>
public sealed class ReadmeModeTableTests
{
    [Fact]
    public void ModeTable_NamesEveryModeOfTheRegistryExceptServe()
    {
        var readme = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "README.md"));
        var expected = Program.ModeRegistry.Select(entry => entry.Name).Where(name => name != "serve")
            .Order(StringComparer.Ordinal).ToList();

        var actual = ModeTableNames(readme).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    ///     The code spans of the first cell of each row of the table under "## The mental model".
    /// </summary>
    private static IEnumerable<string> ModeTableNames(string readme)
    {
        var lines = readme.Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => line.Trim() == "## The mental model");
        Assert.True(start >= 0, "README.md has no \"## The mental model\" section.");

        var rows = lines.Skip(start + 1).SkipWhile(line => !line.TrimStart().StartsWith('|'))
            .TakeWhile(line => line.TrimStart().StartsWith('|')).Skip(2);
        foreach (var row in rows)
        {
            var firstCell = row.Trim().Trim('|').Split('|')[0];
            foreach (var (_, span) in DocReferenceChecker.InlineSpans(firstCell))
                yield return span;
        }
    }
}
