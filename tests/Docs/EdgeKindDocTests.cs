using System.Text.RegularExpressions;
using Lurp.Storage;
using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     D112: <c>docs/ARCHITECTURE.md</c> §4.3 states the meaning, the source and target, and the
///     provenance of each edge kind. The table has one row for each <see cref="EdgeKind" /> and no
///     row for a kind that does not exist.
/// </summary>
public sealed partial class EdgeKindDocTests
{
    private const string Doc = "docs/ARCHITECTURE.md";
    private const string Heading = "### 4.3 Edge kinds";

    [GeneratedRegex(@"^\|\s*`(?<kind>[A-Za-z]+)`\s*\|")]
    private static partial Regex KindRowPattern();

    [Fact]
    public void EdgeKindTable_HasOneRowForEachKind()
    {
        var markdown = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), Doc));
        var rows = TableKinds(markdown).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(Enum.GetNames<EdgeKind>().Order(StringComparer.Ordinal).ToList(), rows);
    }

    private static IEnumerable<string> TableKinds(string markdown)
    {
        var inSection = false;
        foreach (var (_, text) in DocReferenceChecker.ProseLines(markdown))
        {
            if (text.StartsWith('#'))
            {
                inSection = text.Trim() == Heading;
                continue;
            }

            if (inSection && KindRowPattern().Match(text) is { Success: true } match)
                yield return match.Groups["kind"].Value;
        }
    }
}
