using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     S01-F7: the batch collapse unions receiver-type constraints as the persisted-row merge
///     does (<c>EdgeOperationsStore.WriteSplitEdges</c>). Expected values: the union of the
///     variants, each variant once, in ordinal order.
/// </summary>
public sealed class EdgeMergeTests
{
    private static EdgeRecord Call(string provenance, string? constraints) => new()
    {
        SourceSymbolId = "S",
        TargetSymbolId = "T",
        Kind = "Calls",
        Provenance = provenance,
        ReceiverTypeConstraintsJson = constraints
    };

    [Fact]
    public void CollapseBatch_HigherRankWithoutConstraints_KeepsTheConstraintsOfTheLowerRank()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            Call(Provenance.Convention, """[["T:A"]]"""),
            Call(Provenance.CompilerProved, null)
        ]));

        Assert.Equal(Provenance.CompilerProved, edge.Provenance);
        Assert.Equal("""[["T:A"]]""", edge.ReceiverTypeConstraintsJson);
    }

    [Fact]
    public void CollapseBatch_EqualRank_UnionsTheConstraints()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            Call(Provenance.CompilerProved, """[["T:A"]]"""),
            Call(Provenance.CompilerProved, """[["T:B"]]""")
        ]));

        Assert.Equal("""[["T:A"],["T:B"]]""", edge.ReceiverTypeConstraintsJson);
    }
}
