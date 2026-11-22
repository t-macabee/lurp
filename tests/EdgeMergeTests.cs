using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     The batch and persisted-row merges use the same union rule for receiver-type constraints.
///     The union contains each variant once, in ordinal order.
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

    private static EdgeRecord SiteCall(string provenance, string? docPath, int? sl, int? sc, int? el, int? ec) => new()
    {
        SourceSymbolId = "S",
        TargetSymbolId = "T",
        Kind = "Calls",
        Provenance = provenance,
        SourceDocumentPath = docPath,
        SourceStartLine = sl,
        SourceStartColumn = sc,
        SourceEndLine = el,
        SourceEndColumn = ec
    };

    [Fact]
    public void CollapseBatch_EqualRank_KeepsAllSitesInSourceOrder_AndSetsPrimaryToFirst()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            SiteCall(Provenance.CompilerProved, "b.cs", 10, 4, 10, 12),
            SiteCall(Provenance.CompilerProved, "a.cs", 5, 2, 5, 10),
            SiteCall(Provenance.CompilerProved, "a.cs", 2, 8, 2, 16)
        ]));

        Assert.Equal(
            [
                new EdgeSite("a.cs", 2, 8, 2, 16),
                new EdgeSite("a.cs", 5, 2, 5, 10),
                new EdgeSite("b.cs", 10, 4, 10, 12)
            ],
            edge.Sites);
        Assert.Equal("a.cs", edge.SourceDocumentPath);
        Assert.Equal((2, 8, 2, 16), (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
    }

    [Fact]
    public void CollapseBatch_HigherRankReplacesTheShownSites()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            SiteCall(Provenance.NameCandidate, "a.cs", 1, 0, 1, 5),
            SiteCall(Provenance.CompilerProved, "a.cs", 7, 0, 7, 5),
            SiteCall(Provenance.Possible, "a.cs", 0, 0, 0, 5)
        ]));

        Assert.Equal(Provenance.CompilerProved, edge.Provenance);
        Assert.Equal([new EdgeSite("a.cs", 7, 0, 7, 5)], edge.Sites);
    }

    [Fact]
    public void CollapseBatch_KeepsTheEvidenceOfEveryProvenance()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            SiteCall(Provenance.NameCandidate, "a.cs", 1, 0, 1, 5),
            SiteCall(Provenance.CompilerProved, "a.cs", 7, 0, 7, 5),
            SiteCall(Provenance.Possible, "a.cs", 0, 0, 0, 5)
        ]));

        Assert.Equal(
            new[] { (Provenance.CompilerProved, 7), (Provenance.NameCandidate, 1), (Provenance.Possible, 0) },
            edge.Evidence.Select(evidence => (evidence.Provenance, evidence.Site.StartLine))
                .OrderBy(item => item.Provenance, StringComparer.Ordinal));
        Assert.Equal([new EdgeSite("a.cs", 7, 0, 7, 5)], edge.Sites);
    }

    [Fact]
    public void CollapseBatch_NullLocation_HasEmptySites()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            SiteCall(Provenance.CompilerProved, null, null, null, null, null)
        ]));

        Assert.Empty(edge.Sites);
    }

    [Fact]
    public void CollapseBatch_FiveThousandSitesSameKey_CollapsesInSourceOrder()
    {
        const int siteCount = 5000;
        var batch = new List<EdgeRecord>(siteCount + 10);
        for (var i = siteCount - 1; i >= 0; i--)
            batch.Add(SiteCall(Provenance.CompilerProved, "a.cs", i, 0, i, 5));
        // Duplicate sites and lower-rank evidence add no shown sites.
        batch.Add(SiteCall(Provenance.CompilerProved, "a.cs", 0, 0, 0, 5));
        batch.Add(SiteCall(Provenance.NameCandidate, "z.cs", 1, 0, 1, 5));

        var edge = Assert.Single(EdgeMerge.CollapseBatch(batch));

        Assert.Equal(siteCount, edge.Sites.Count);
        Assert.Equal(new EdgeSite("a.cs", 0, 0, 0, 5), edge.Sites[0]);
        Assert.Equal(new EdgeSite("a.cs", siteCount - 1, 0, siteCount - 1, 5), edge.Sites[^1]);
        Assert.Equal("a.cs", edge.SourceDocumentPath);
        Assert.Equal(0, edge.SourceStartLine);
    }

    [Fact]
    public void CollapseBatch_HigherRankAfterLowerRank_ReplacesSitesAndMergesPayloads()
    {
        var edge = Assert.Single(EdgeMerge.CollapseBatch(
        [
            new EdgeRecord
            {
                SourceSymbolId = "S",
                TargetSymbolId = "T",
                Kind = "MayDispatchTo",
                Provenance = Provenance.Possible,
                SourceDocumentPath = "low.cs",
                SourceStartLine = 1,
                SourceStartColumn = 0,
                SourceEndLine = 1,
                SourceEndColumn = 5,
                TypeArgumentsJson = """["T:Low"]""",
                ReceiverTypeConstraintsJson = """[["T:R1"]]"""
            },
            new EdgeRecord
            {
                SourceSymbolId = "S",
                TargetSymbolId = "T",
                Kind = "MayDispatchTo",
                Provenance = Provenance.CompilerProved,
                SourceDocumentPath = "high.cs",
                SourceStartLine = 9,
                SourceStartColumn = 0,
                SourceEndLine = 9,
                SourceEndColumn = 5,
                TypeArgumentsJson = null,
                ReceiverTypeConstraintsJson = """[["T:R2"]]"""
            }
        ]));

        Assert.Equal(Provenance.CompilerProved, edge.Provenance);
        Assert.Equal([new EdgeSite("high.cs", 9, 0, 9, 5)], edge.Sites);
        Assert.Equal("""[["T:Low"]]""", edge.TypeArgumentsJson);
        Assert.Equal("""[["T:R1"],["T:R2"]]""", edge.ReceiverTypeConstraintsJson);
    }

    [Fact]
    public void WithSites_SameSites_ReturnsTheSameEdge()
    {
        var winner = SiteCall(Provenance.CompilerProved, "a.cs", 2, 0, 2, 5);
        var normalizedWinner = winner.WithSites([new EdgeSite("a.cs", 2, 0, 2, 5)]);
        Assert.Same(normalizedWinner, normalizedWinner.WithSites([new EdgeSite("a.cs", 2, 0, 2, 5)]));
    }
}
