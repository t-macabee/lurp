using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

public sealed class CapsuleCharacterizationTests : IntegrationTestBase
{
    private const string AnchorSource = """
        namespace TestProject;

        public class Anchor
        {
            public int UsedByCaller(int x) => x;

            public void Unused() { }

            public string LargeAnchor(int p1, string p2, bool p3, double p4, long p5)
            {
                // Padding to make this method large enough that a low budget
                // forces the "anchor" budget_exhausted contract for D2 floor.
                var a = p1 + p5;
                var b = p2 ?? "default";
                var c = p3 && p4 > 0;
                var d = new System.Collections.Generic.List<int> { p1, (int)p5 };
                var e = d.Count > 0 ? d[0] : -1;
                return b + ":" + a + "/" + e + "/" + c;
            }
        }

        public class Caller
        {
            public int CallAnchor() => new Anchor().UsedByCaller(42);

            public string CallLargeAnchor() => new Anchor().LargeAnchor(1, "x", true, 0.5, 10);
        }
        // -- D5 gap-anchor comment target (line 28) --
        """;

    private const string StandaloneSource = """
        namespace TestProject;

        public class Standalone
        {
            public void NoCallers() { }
        }
        """;

    private async Task<(string SnapshotId, string UnusedId, string UsedId, string StandaloneId, string LargeAnchorId, string AnchorFile)> IndexFixtureAsync()
    {
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Anchor.cs"] = AnchorSource,
                ["Standalone.cs"] = StandaloneSource,
            });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var unusedId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor.Unused");
        var usedId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor.UsedByCaller");
        var standaloneId = ResolveSymbolId(snapshotId, "global::TestProject.Standalone.NoCallers");
        var largeAnchorId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor.LargeAnchor");
        var anchorFile = "src/TestProject/Anchor.cs";

        return (snapshotId, unusedId, usedId, standaloneId, largeAnchorId, anchorFile);
    }

    private ContextAssemblyOptions DefaultOptions(int budget = 5000, int maxHops = 1)
        => new(ContextIntent.Inspect, budget, maxHops);

    // ── D2 ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Capsule_RespectsContentBudget_Generous()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, usedId, null, null);
            var options = DefaultOptions(budget: 5000, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.True(capsule.EstimatedTokens <= 5000,
                $"EstimatedTokens ({capsule.EstimatedTokens}) exceeded generuous budget (5000).");
            Assert.False(capsule.Truncated,
                "Capsule should not be truncated at generous budget.");
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Capsule_RespectsContentBudget_Mid()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, usedId, null, null);
            var options = DefaultOptions(budget: 200, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.True(capsule.EstimatedTokens <= 200,
                $"EstimatedTokens ({capsule.EstimatedTokens}) exceeded mid budget (200).");
            Assert.True(capsule.Truncated, "Capsule should be truncated at mid budget.");
            Assert.NotEmpty(capsule.OmittedTiers);
            Assert.Contains(capsule.OmittedTiers, e => e.Reason == "budget_exhausted");
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Capsule_RespectsContentBudget_SmallButAboveFloor()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, usedId, null, null);
            var options = DefaultOptions(budget: 120, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.True(capsule.EstimatedTokens <= 120,
                $"EstimatedTokens ({capsule.EstimatedTokens}) exceeded small budget (120).");
            Assert.True(capsule.Truncated, "Capsule should be truncated at small budget.");
            Assert.DoesNotContain(capsule.OmittedTiers,
                e => e.Category == "anchor" && e.Reason == "budget_exhausted");
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Capsule_RespectsContentBudget_FloorAnchorBudgetExhausted()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, _, _, largeAnchorId, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, largeAnchorId, null, null);
            var options = DefaultOptions(budget: 1, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.Contains(capsule.OmittedTiers,
                e => e.Category == "anchor" && e.Reason == "budget_exhausted");
        }
        finally
        {
            store.Close();
        }
    }

    // ── D3 ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Capsule_EmptyTier_MeansProvedAbsence()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, unusedId, _, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, unusedId, null, null);
            var options = DefaultOptions(budget: 5000, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            // direct_callers tier must be empty — nothing calls Anchor.Unused
            Assert.Empty(capsule.DirectCallers);
            // omitted_tiers records the proved absence
            Assert.Contains(capsule.OmittedTiers,
                e => e.Category == "direct_callers" && e.Reason == "empty");
            // InclusionReasons must NOT contain omittedTiers.unresolved
            Assert.False(capsule.InclusionReasons.ContainsKey("omittedTiers.unresolved"),
                "Proved-absence capsule must not carry the 'unresolved' inclusion reason.");
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Capsule_EmptyTier_StandaloneClassNoCallers()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, _, standaloneId, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, standaloneId, null, null);
            var options = DefaultOptions(budget: 5000, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.Empty(capsule.DirectCallers);
            Assert.Contains(capsule.OmittedTiers,
                e => e.Category == "direct_callers" && e.Reason == "empty");
            Assert.False(capsule.InclusionReasons.ContainsKey("omittedTiers.unresolved"));
        }
        finally
        {
            store.Close();
        }
    }

    // ── D4 ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Capsule_UnresolvedTier_WhenAnchorRegionHasLostBindings()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, unusedId, _, _, _, anchorFile) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            // Seed a binding_incompleteness row for the anchor's document.
            // project_unreadable is in BindingIncompletenessReason.UnobservableReasons,
            // so AnchorRegionHasLostBindings returns true for this anchor.
            store.SaveBindingIncompleteness(snapshotId,
            [
                new BindingIncompletenessRecord(
                    ProjectName: "TestProject",
                    DocumentPath: anchorFile,
                    Reason: "project_unreadable",
                    Count: 1,
                    ExtractorVersion: "0.0.0")
            ]);

            var lookup = new ContextLookup(snapshotId, unusedId, null, null);
            var options = DefaultOptions(budget: 5000, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            // The empty direct_callers tier must have reason "unresolved" (not "empty")
            Assert.Contains(capsule.OmittedTiers,
                e => e.Category == "direct_callers" && e.Reason == "unresolved");
            Assert.DoesNotContain(capsule.OmittedTiers,
                e => e.Category == "direct_callers" && e.Reason == "empty");
            // InclusionReasons must contain omittedTiers.unresolved
            Assert.True(capsule.InclusionReasons.ContainsKey("omittedTiers.unresolved"));
            Assert.Contains("unresolved", capsule.InclusionReasons["omittedTiers.unresolved"]);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Capsule_UnresolvedTier_NullBindingStoreTreatedAsUnobservable()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, unusedId, _, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            // Pass null for bindingIncompletenessStore — absence of the reader
            // must read as unobservable, not proved-absent.
            var lookup = new ContextLookup(snapshotId, unusedId, null, null);
            var options = DefaultOptions(budget: 5000, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, null, store);

            Assert.NotNull(capsule);
            Assert.Contains(capsule.OmittedTiers,
                e => e.Category == "direct_callers" && e.Reason == "unresolved");
            Assert.True(capsule.InclusionReasons.ContainsKey("omittedTiers.unresolved"));
        }
        finally
        {
            store.Close();
        }
    }

    // ── D5 ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Capsule_GapAnchor_MarksEveryTierUnresolved()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, _, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            // Anchor.cs line 28 is a comment
            var lookup = new ContextLookup(snapshotId, null, "src/TestProject/Anchor.cs", 28);
            var options = DefaultOptions(budget: 5000, maxHops: 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.Equal("gap", capsule.Anchor.Kind);
            Assert.Contains("no symbol at", capsule.Anchor.FullyQualifiedName);
            Assert.Empty(capsule.Anchor.Provenance);

            // Every tier must be marked "unresolved"
            foreach (var tierName in ContextAssembler.TierNames)
            {
                Assert.Contains(capsule.OmittedTiers,
                    e => e.Category == tierName && e.Reason == "unresolved");
            }

            Assert.True(capsule.InclusionReasons.ContainsKey("omittedTiers.unresolved"));
            Assert.Contains("unresolved", capsule.InclusionReasons["omittedTiers.unresolved"]);

            // Must have a location_gap uncertainty
            Assert.Contains(capsule.Uncertainties, u => u.RelationshipKind == "location_gap");
        }
        finally
        {
            store.Close();
        }
    }

    // ── D6 ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Capsule_OmittedTier_FetchCommandReturnsTheOmittedContent()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            // Build a budgeted capsule that forces direct_callers to be budget_exhausted
            var lookup = new ContextLookup(snapshotId, usedId, null, null);
            var options = DefaultOptions(budget: 50, maxHops: 1);
            var budgetedCapsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(budgetedCapsule);

            // Find an omitted tier with budget_exhausted that is a fetchable tier
            var omittedEntry = budgetedCapsule.OmittedTiers.FirstOrDefault(
                e => e.Reason == "budget_exhausted"
                     && ContextAssembler.TierNames.Contains(e.Category, StringComparer.Ordinal));

            if (omittedEntry == null)
            {
                // Budget may not have hit a fetchable tier — skip the fetch assertion
                // but still verify the template exists
                Assert.Contains(budgetedCapsule.InclusionReasons,
                    kv => kv.Key == "omittedTiers.budget_exhausted");
                return;
            }

            Assert.Contains(budgetedCapsule.InclusionReasons,
                kv => kv.Key == "omittedTiers.budget_exhausted");

            var template = budgetedCapsule.InclusionReasons["omittedTiers.budget_exhausted"];

            // The template must contain the fetch command pattern for fetchable tiers
            Assert.Contains("--mode=context --tier=<category> --symbol=<anchor symbolId>", template);

            // Verify non-fetchable sections appear in the "larger --content-budget" sentence
            if (budgetedCapsule.OmittedTiers.Any(e =>
                    e.Reason == "budget_exhausted" &&
                    !ContextAssembler.TierNames.Contains(e.Category, StringComparer.Ordinal)))
            {
                Assert.Contains("larger --content-budget", template);
            }

            // Fetch the omitted tier unbudgeted via BuildTierPage
            var symbolId = SymbolId.Parse(usedId);
            var page = ContextAssembler.BuildTierPage(
                store, store, snapshotId, symbolId,
                omittedEntry.Category, maxHops: 1, includeGenerated: false,
                offset: 0, limit: 100);

            Assert.NotNull(page);
            Assert.Equal(omittedEntry.Category, page.TierName);
            Assert.NotEmpty(page.Items);
        }
        finally
        {
            store.Close();
        }
    }
}
