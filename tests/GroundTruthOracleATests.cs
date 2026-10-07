using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     Audit B1 Phase 7 exact-set gate: the lowered-call facts the extractor
///     writes (initializers, top-level statements, operators, partial-method
///     implementations, method groups, event subscriptions, implicit calls and
///     conversions) must equal Oracle A's operation-derived fact set over the
///     whole CallShapes corpus — no missing fact, no extra fact.
/// </summary>
[Trait("Category", "Slow")]
[Collection("GroundTruth")]
public sealed class GroundTruthOracleATests(GroundTruthFixture callShapes, CrossProjectGroundTruthFixture crossProject)
{
    // The body-lineage extractors whose facts Oracle A models: the syntax
    // extractors (Calls, Constructs, Reads/Writes) plus the Phase 7 operation
    // walk. Structural/parameter references and adapter facts are separate
    // lineages with their own tests.
    private static readonly string[] ReferenceShapeExtractors =
    [
        ExtractorConstants.CallsExtractor,
        ExtractorConstants.ConstructsExtractor,
        ExtractorConstants.ReadsWritesExtractor,
        ExtractorConstants.OperationShapesExtractor
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationShapeFacts_ExactlyMatchOracleA(bool useCrossProject)
    {
        GroundTruthFixtureBase fixture = useCrossProject ? crossProject : callShapes;
        var expected = OperationShapeOracle.Extract(fixture.Compilations);
        Assert.NotEmpty(expected);

        var actual = fixture.Facts
            .Where(fact => ReferenceShapeExtractors.Contains(fact.ExtractorVersion) &&
                           fact.SourceDeclared && fact.TargetDeclared)
            .Select(fact => (fact.Source, fact.Kind, fact.Target))
            .ToHashSet();

        // The edge merge keeps one lineage per (source, kind, target). A body fact
        // that an earlier extractor also emits (for example a method's parameter type,
        // which ParameterDependencyEdgeExtractor also emits) is stored under that
        // extractor's lineage, so the missing check reads every lineage.
        var present = fixture.Facts
            .Where(fact => fact.SourceDeclared && fact.TargetDeclared)
            .Select(fact => (fact.Source, fact.Kind, fact.Target))
            .ToHashSet();

        var missing = expected.Except(present).OrderBy(fact => fact).ToList();
        var extra = actual.Except(expected).OrderBy(fact => fact).ToList();

        Console.WriteLine(
            $"Oracle A fixture ({(useCrossProject ? "CrossProject" : "CallShapes")}): " +
            $"{fixture.Compilations.Count} compilation(s), expected={expected.Count}, actual={actual.Count}, " +
            $"present={present.Count}, missing={missing.Count}, extra={extra.Count}.");

        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"Oracle A mismatch: {missing.Count} missing, {extra.Count} extra. " +
            $"expected={expected.Count} actual={actual.Count}{Environment.NewLine}" +
            $"MISSING:{Environment.NewLine}{Format(missing)}{Environment.NewLine}" +
            $"EXTRA:{Environment.NewLine}{Format(extra)}");
    }

    private static string Format(IEnumerable<(string Source, string Kind, string Target)> facts)
    {
        return string.Join(Environment.NewLine, facts.Select(fact => $"  {fact.Source} -[{fact.Kind}]-> {fact.Target}"));
    }
}
