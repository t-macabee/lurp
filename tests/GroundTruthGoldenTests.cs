namespace Lurp.Tests;

/// <summary>
///     Oracle C (D18): the golden file of a corpus lists every fact of its compared kinds whose
///     source and target are both declared in the corpus. The extractor output in that scope must
///     equal the golden as an exact set: no missing fact and no extra fact. A golden fact with a
///     'pending' field is true, but the extractor does not write it yet. It must be absent, so the
///     stage that makes the extractor write it also removes the field.
/// </summary>
[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class GroundTruthGoldenTests(GroundTruthFixture callShapes, CrossProjectGroundTruthFixture crossProject)
{
    private const string CallShapesGolden = "CallShapes/call-shapes.golden.json";
    private const string CrossProjectGolden = "CrossProject/cross-project.golden.json";

    [Theory]
    [InlineData(CallShapesGolden)]
    [InlineData(CrossProjectGolden)]
    public void ExtractedFacts_EqualTheGolden(string goldenPath)
    {
        GroundTruthFixtureBase fixture = goldenPath == CallShapesGolden ? callShapes : crossProject;
        if (fixture.Facts.Count == 0)
            throw new InvalidOperationException("The fixture extracted zero facts; the extraction pipeline did not run.");

        var golden = GoldenFile.Load(goldenPath);
        var kinds = golden.ComparedKinds.ToHashSet(StringComparer.Ordinal);
        var extracted = fixture.Facts
            .Where(fact => fact.SourceDeclared && fact.TargetDeclared && kinds.Contains(fact.Kind))
            .Select(fact => (fact.Source, fact.Kind, fact.Target))
            .ToHashSet();
        var expected = golden.Facts.Where(fact => fact.Pending is null)
            .ToDictionary(fact => (fact.Source, fact.Kind, fact.Target));
        var pending = golden.Facts.Where(fact => fact.Pending is not null)
            .ToDictionary(fact => (fact.Source, fact.Kind, fact.Target));

        var missing = expected.Where(entry => !extracted.Contains(entry.Key)).Select(entry => entry.Value).ToList();
        var extra = extracted.Where(key => !expected.ContainsKey(key) && !pending.ContainsKey(key))
            .Order().ToList();
        var pendingWritten = pending.Where(entry => extracted.Contains(entry.Key)).Select(entry => entry.Value).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0 && pendingWritten.Count == 0,
            $"{goldenPath}: {missing.Count} missing, {extra.Count} extra, {pendingWritten.Count} pending fact(s) now written." +
            $"{Environment.NewLine}MISSING (the extractor does not write a golden fact):{Environment.NewLine}" +
            string.Join(Environment.NewLine, missing.Select(fact => $"  {fact.Id} {fact.Source} -[{fact.Kind}]-> {fact.Target}  ({fact.Reason})")) +
            $"{Environment.NewLine}EXTRA (no golden fact; derive it by hand, then add it or fix the extractor):{Environment.NewLine}" +
            string.Join(Environment.NewLine, extra.Select(fact => $"  {fact.Source} -[{fact.Kind}]-> {fact.Target}")) +
            $"{Environment.NewLine}PENDING NOW WRITTEN (remove the 'pending' field):{Environment.NewLine}" +
            string.Join(Environment.NewLine, pendingWritten.Select(fact => $"  {fact.Id} {fact.Source} -[{fact.Kind}]-> {fact.Target}  ({fact.Pending!.Stage}, {fact.Pending.Tracking})")));
    }

    [Theory]
    [InlineData(CallShapesGolden)]
    [InlineData(CrossProjectGolden)]
    public void EveryGoldenFact_IsUnique_AndHasAKnownKindAndBasis(string goldenPath)
    {
        var golden = GoldenFile.Load(goldenPath);
        var problems = new List<string>();

        problems.AddRange(golden.Facts.GroupBy(fact => fact.Id).Where(group => group.Count() > 1)
            .Select(group => $"id {group.Key} is used {group.Count()} times"));
        problems.AddRange(golden.Facts.GroupBy(fact => (fact.Source, fact.Kind, fact.Target)).Where(group => group.Count() > 1)
            .Select(group => $"{string.Join(", ", group.Select(fact => fact.Id))} state the same fact"));
        problems.AddRange(golden.Facts.Where(fact => !golden.ComparedKinds.Contains(fact.Kind))
            .Select(fact => $"{fact.Id}: kind '{fact.Kind}' is not in compared_kinds"));
        problems.AddRange(golden.Facts.Where(fact => !golden.BasisValues.ContainsKey(fact.Basis))
            .Select(fact => $"{fact.Id}: basis '{fact.Basis}' is not in basis_values"));
        problems.AddRange(golden.Facts.Where(fact => fact.Reason.Length == 0)
            .Select(fact => $"{fact.Id}: the reason is empty"));
        problems.AddRange(golden.Facts.Where(fact => fact.Pending is { } pending &&
                                                     (pending.Stage.Length == 0 || pending.Tracking.Length == 0 || pending.Reason.Length == 0))
            .Select(fact => $"{fact.Id}: a pending field needs a stage, a tracking id and a reason"));
        problems.AddRange(golden.BasisValues.Keys.Where(basis => golden.Facts.All(fact => fact.Basis != basis))
            .Select(basis => $"basis value '{basis}' is not used by any fact"));

        Assert.True(problems.Count == 0, $"{goldenPath}:{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", problems)}");
    }
}
