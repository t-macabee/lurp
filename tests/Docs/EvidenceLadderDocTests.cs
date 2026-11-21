using Lurp.Storage;
using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class EvidenceLadderDocTests
{
    [Fact]
    public void EvidenceLadder_HasTheProvenanceLevelsInRankOrder()
    {
        var markdown = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "docs/ARCHITECTURE.md"));
        var levels = DocTable.RowNames(markdown, "### 4.2 Evidence ladder");

        Assert.Equal(Provenance.All, levels);
    }
}
