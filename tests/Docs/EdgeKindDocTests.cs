using Lurp.Storage;
using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     D112: <c>docs/ARCHITECTURE.md</c> §4.3 states the meaning, the source and target, and the
///     provenance of each edge kind. The table has one row for each <see cref="EdgeKind" /> and no
///     row for a kind that does not exist.
/// </summary>
public sealed class EdgeKindDocTests
{
    private const string Doc = "docs/ARCHITECTURE.md";
    private const string Heading = "### 4.3 Edge kinds";

    [Fact]
    public void EdgeKindTable_HasOneRowForEachKind()
    {
        var markdown = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), Doc));
        var rows = DocTable.RowNames(markdown, Heading).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(Enum.GetNames<EdgeKind>().Order(StringComparer.Ordinal).ToList(), rows);
    }
}
