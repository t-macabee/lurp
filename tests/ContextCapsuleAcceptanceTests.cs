using Lurp.Storage;
using Lurp.Workspace;
using Xunit;
using Xunit.Sdk;

namespace Lurp.Storage.Tests;

public sealed class ContextCapsuleAcceptanceTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"lurp-capsule-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }

    [SkippableFact]
    public async Task SelfHost_EdgeLocationResolver_CapsuleSatisfiesPhase15Contract()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(), "MSBuild is not available on this system.");
        Directory.CreateDirectory(_outputDir);
        var repositoryRoot = LocateRepositoryRoot();
        var solutionPath = Path.Combine(repositoryRoot, "Lurp.slnx");
        var dbPath = Path.Combine(_outputDir, "index.db");
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, _outputDir);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var anchor = store.ResolveSymbolByFqn("Lurp.Shared.EdgeLocationResolver", snapshotId)
            ?? throw new InvalidOperationException("Self-host anchor was not indexed.");
        var target = new ImpactPath(
        [
            new ImpactHop(anchor.SymbolId.Value, anchor.SymbolId.Value, "target_topology", "caller_supplied"),
        ]);
        var capsule = ContextAssembler.ResolveAndAssemble(
            store,
            store,
            new ContextLookup(snapshotId, anchor.SymbolId.Value, null, null),
            new ContextAssemblyOptions(
                ContextIntent.Modify,
                // Generous enough to hold the full self-host capsule untrimmed
                // (its type-anchor tiers carry member source bodies). This test
                // proves the eleven-item Phase 15 contract is present, not that
                // the budget truncates; budget truthfulness is exercised by
                // CapsuleBudgetEnforcerTests and the --budget CLI criterion.
                Budget: 500_000,
                MaxHops: 3,
                Scope: "src/Shared/EdgeLocationResolver.cs",
                AffectedProjects: ["Lurp"],
                ChangeObjective: "Preserve the explicit git-root dependency boundary.",
                CallerConstraints: ["Do not introduce a Workspace dependency."],
                TargetTopology: [target],
                TopologyAnnotations: ["caller-supplied target topology"],
                GitRoot: repositoryRoot),
            store,
            store);

        Assert.Equal(snapshotId, capsule.Anchor.SnapshotId);
        Assert.NotEmpty(capsule.Anchor.Locations);
        AssertSectionPresentOrOmitted(capsule, "contracts", capsule.Contracts);
        AssertSectionPresentOrOmitted(capsule, "registeredImplementations", capsule.RegisteredImplementations);
        AssertSectionPresentOrOmitted(capsule, "relevantTests", capsule.RelevantTests);
        Assert.NotNull(capsule.Uncertainties);
        Assert.True(capsule.IncomingPaths.Count + capsule.OutgoingPaths.Count > 0);
        Assert.Contains(capsule.SuggestedVerification, step =>
            !string.IsNullOrWhiteSpace(step.Command) && step.Command.Contains("dotnet test", StringComparison.Ordinal));
        Assert.NotEmpty(capsule.LikelyChangeSites);
        Assert.All(AllItems(capsule).Where(item => item.Source != null), item => Assert.NotNull(item.InclusionReason));
        AssertSectionPresentOrOmitted(capsule, "affectedPublicSurfaces", capsule.AffectedPublicSurfaces);
        Assert.NotEmpty(capsule.InclusionReasons);
        Assert.Contains(capsule.Constraints, constraint => constraint.Origin == "caller_supplied");
        var topology = Assert.IsType<CapsuleTopology>(capsule.Topology);
        Assert.NotNull(topology.Current);
        Assert.True(topology.Current.TotalHopCount > 0);
        Assert.NotEmpty(topology.Target);
        Assert.NotNull(capsule.Completeness);
    }

    // A section that is genuinely empty for this anchor must prove it through
    // the reason-coded OmittedTiers channel instead of a vacuous Assert.NotNull
    // on a collection that is non-null by construction. The reason is "empty"
    // when the region's bindings were observable, "unresolved" when the anchor
    // sits in a region where bindings were lost — both are honest reason-coded
    // omissions, and which one applies depends on the persisted completeness.
    private static void AssertSectionPresentOrOmitted(
        ContextCapsule capsule, string category, List<CapsuleItem> items)
    {
        if (items.Count > 0)
            return;
        Assert.Contains(capsule.OmittedTiers,
            entry => entry.Category == category && entry.Reason is "empty" or "unresolved");
    }

    private static IEnumerable<CapsuleItem> AllItems(ContextCapsule capsule)
        => capsule.Contracts
            .Concat(capsule.DirectCallees)
            .Concat(capsule.DirectCallers)
            .Concat(capsule.RegisteredImplementations)
            .Concat(capsule.RelevantTests)
            .Concat(capsule.SecondDegreeContext)
            .Concat(capsule.SurroundingSource);

    private static string LocateRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lurp.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
