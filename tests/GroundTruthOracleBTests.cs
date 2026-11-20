using Lurp.Parity.Shared;

namespace Lurp.Tests;

/// <summary>
///     Oracle B checks callers of ordinary methods, constructors, operators, conversions,
///     explicit interface implementations, and property and event accessors. Static abstract
///     members use the same dispatch family rule as other methods. An event assignment must
///     have a Writes edge to its event. A property access can match a Reads or Writes edge
///     to its property. The test reports precision and requires full recall for checked callers.
/// </summary>
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

        var result = await SymbolFinderOracle.CompareAsync(solution, edges, SymbolDocIds.Normalized);
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
            $"skipped {summary.SkippedImplicit} implicit member(s), {summary.SkippedNoId} without doc id. " +
            $"Dynamic callers set aside: {summary.SetAsideCallers.Count(entry => entry.Reason == SymbolFinderOracle.DynamicBindingReason)}.");

        Assert.True(summary.CheckedPairs > 0, "SymbolFinder returned no caller pairs; the oracle did not run.");
        Assert.True(missLines.Count == 0,
            $"{missLines.Count} SymbolFinder caller(s) missing from extraction (checked {summary.CheckedPairs} pairs):{Environment.NewLine}" +
            string.Join(Environment.NewLine, missLines.Take(60)));
    }
}
