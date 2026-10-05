namespace Lurp.Tests;

/// <summary>
///     Audit B1 Oracle C gate: every reviewed fact in
///     <c>call-shapes.golden.json</c> must be extracted from the fixture by the
///     real pipeline. A missing fact fails with the full list; extra extraction
///     facts are the exact-set test's job.
/// </summary>
[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class GroundTruthGoldenTests(GroundTruthFixture fixture)
{
    [Fact]
    public void EveryGoldenFact_IsExtracted()
    {
        if (fixture.Facts.Count == 0)
            throw new InvalidOperationException("The fixture extracted zero facts; the extraction pipeline did not run.");

        var extracted = fixture.Facts
            .Where(fact => fact.SourceDeclared && fact.TargetDeclared)
            .Select(fact => (fact.Source, fact.Kind, fact.Target))
            .ToHashSet();

        var missing = GroundTruthFixture.LoadGolden()
            .Where(fact => !extracted.Contains((fact.Source, fact.Kind, fact.Target)))
            .ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} golden fact(s) missing from extraction:{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Select(fact => $"  {fact.Id} {fact.Source} -[{fact.Kind}]-> {fact.Target}  ({fact.Reason})")));
    }
}
