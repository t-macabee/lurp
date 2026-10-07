using Lurp.Parity.Shared;

namespace Lurp.Tests;

/// <summary>
///     Audit B1 Oracle B: Roslyn's own <c>SymbolFinder.FindCallersAsync</c>
///     is the caller oracle. For every method-like member the extractor models —
///     ordinary methods, constructors (including primary constructors),
///     user-defined operators, conversions, and property/event accessors — each
///     caller Roslyn reports must appear in extraction as a Calls or
///     MethodGroupRef relation: recall must be 100% for these compiler-proved
///     relations. A constructor target needs a Calls edge to the constructor
///     itself. Accessor targets are mapped to the owner ids the extractor uses: a
///     getter/setter to Reads/Writes on its property, and an event add/remove to
///     Writes on its event. The test also prints the precision of those relations
///     and the first 20 extracted Calls/MethodGroupRef edges to checked targets
///     that Roslyn did not report (precision is informational).
/// </summary>
/// <remarks>
///     Caller identity is normalized to the extraction owner: an auto-property
///     backing field maps to its property, a property maps to its accessors, and
///     a local function maps to its containing method. Static-abstract dispatch
///     (a generic method constrained to a static abstract interface member) is a
///     dispatch relation, not a direct call, and is reported as a skipped target.
/// </remarks>
[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class GroundTruthOracleBTests(GroundTruthFixture callShapes, CrossProjectGroundTruthFixture crossProject)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EverySymbolFinderCaller_IsExtracted(bool useCrossProject)
    {
        GroundTruthFixtureBase fixture = useCrossProject ? crossProject : callShapes;
        var solution = fixture.Solution ?? throw new InvalidOperationException("Solution not loaded.");
        var edges = fixture.Facts
            .Where(fact => fact.SourceDeclared && fact.TargetDeclared)
            .Select(fact => (fact.Source, fact.Kind, fact.Target))
            .ToHashSet();

        var result = await SymbolFinderOracle.CompareAsync(solution, edges, OperationShapeOracle.NormalizedDocId);
        var targets = result.Targets;
        var summary = result.Summary;
        var extras = result.Extras;
        var missLines = targets
            .SelectMany(static target => target.MissingCallers)
            .Select(static missing =>
                $"{missing.TargetKind} {string.Join(" OR ", missing.CallerIds)} " +
                $"-[{string.Join("|", missing.Lines)}]-> {missing.TargetId}")
            .ToList();

        Assert.NotEmpty(targets);

        // Fix (5): precision is the share of extracted Calls/MethodGroupRef edges
        // to checked targets that SymbolFinder did report as callers; the rest are
        // printed so an oracle/extractor divergence is visible. Not asserted.
        Console.WriteLine(
            $"Oracle B precision: {summary.Precision:P2} ({summary.MatchedPairs} matched SymbolFinder pairs, {extras.Count} " +
            $"extracted Calls/MethodGroupRef edge(s) to checked targets that SymbolFinder did not report).");
        foreach (var edge in extras.Take(20))
            Console.WriteLine($"  extra edge: {edge.Source} -[{edge.Kind}]-> {edge.Target}");

        var kindSummary = string.Join(", ", SymbolFinderOracle.TargetKinds.Select(kind =>
            $"{kind}={summary.TargetCounts.GetValueOrDefault(kind)} target(s)/{summary.MissCounts.GetValueOrDefault(kind)} miss(es)"));
        Console.WriteLine(
            $"Oracle B recall: checked {summary.CheckedPairs} caller pair(s) against {targets.Count} target(s) ({kindSummary}); " +
            $"skipped {summary.SkippedImplicit} implicit member(s), {summary.SkippedNoId} without doc id, " +
            $"{summary.SkippedStaticAbstract.Count} static-abstract dispatch target(s).");
        foreach (var method in summary.SkippedStaticAbstract)
            Console.WriteLine($"  skipped static-abstract: {method} " +
                              $"(registered boundary '{SymbolFinderOracle.StaticAbstractDispatchBoundaryId}')");

        Assert.True(summary.CheckedPairs > 0, "SymbolFinder returned no caller pairs; the oracle did not run.");
        Assert.True(missLines.Count == 0,
            $"{missLines.Count} SymbolFinder caller(s) missing from extraction (checked {summary.CheckedPairs} pairs):{Environment.NewLine}" +
            string.Join(Environment.NewLine, missLines.Take(60)));
    }
}
