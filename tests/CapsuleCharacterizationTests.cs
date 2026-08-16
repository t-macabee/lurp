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

    private async
        Task<(string SnapshotId, string UnusedId, string UsedId, string StandaloneId, string LargeAnchorId, string
            AnchorFile)> IndexFixtureAsync()
    {
        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Anchor.cs"] = AnchorSource,
                ["Standalone.cs"] = StandaloneSource
            });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var unusedId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor.Unused");
        var usedId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor.UsedByCaller");
        var standaloneId = ResolveSymbolId(snapshotId, "global::TestProject.Standalone.NoCallers");
        var largeAnchorId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor.LargeAnchor");
        const string anchorFile = "src/TestProject/Anchor.cs";

        return (snapshotId, unusedId, usedId, standaloneId, largeAnchorId, anchorFile);
    }

    private static ContextAssemblyOptions DefaultOptions(int budget = 5000, int maxHops = 1)
    {
        return new ContextAssemblyOptions(ContextIntent.Inspect, budget, maxHops);
    }

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
            var options = DefaultOptions(5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.True(capsule.EstimatedTokens <= 5000,
                $"EstimatedTokens ({capsule.EstimatedTokens}) exceeded generous budget (5000).");
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
            var options = DefaultOptions(200, 1);
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
            var options = DefaultOptions(120, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.True(capsule.EstimatedTokens <= 120,
                $"EstimatedTokens ({capsule.EstimatedTokens}) exceeded small budget (120).");
            Assert.True(capsule.Truncated, "Capsule should be truncated at small budget.");
            Assert.DoesNotContain(capsule.OmittedTiers,
                e => e is { Category: "anchor", Reason: "budget_exhausted" });
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
            var options = DefaultOptions(1, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.Contains(capsule.OmittedTiers,
                e => e is { Category: "anchor", Reason: "budget_exhausted" });
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
            var options = DefaultOptions(5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            // direct_callers tier must be empty — nothing calls Anchor.Unused
            Assert.Empty(capsule.DirectCallers);
            // omitted_tiers records the proved absence
            Assert.Contains(capsule.OmittedTiers,
                e => e is { Category: "direct_callers", Reason: "empty" });
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
            var options = DefaultOptions(5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.Empty(capsule.DirectCallers);
            Assert.Contains(capsule.OmittedTiers,
                e => e is { Category: "direct_callers", Reason: "empty" });
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
                    "TestProject",
                    anchorFile,
                    "project_unreadable",
                    1,
                    "0.0.0")
            ]);

            var lookup = new ContextLookup(snapshotId, unusedId, null, null);
            var options = DefaultOptions(5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            // The empty direct_callers tier must have reason "unresolved" (not "empty")
            Assert.Contains(capsule.OmittedTiers,
                e => e is { Category: "direct_callers", Reason: "unresolved" });
            Assert.DoesNotContain(capsule.OmittedTiers,
                e => e is { Category: "direct_callers", Reason: "empty" });
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
            var options = DefaultOptions(5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, null, store);

            Assert.NotNull(capsule);
            Assert.Contains(capsule.OmittedTiers,
                e => e is { Category: "direct_callers", Reason: "unresolved" });
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
            var options = DefaultOptions(5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.Equal("gap", capsule.Anchor.Kind);
            Assert.Contains("no symbol at", capsule.Anchor.FullyQualifiedName);
            Assert.Empty(capsule.Anchor.Provenance);

            // Every tier must be marked "unresolved"
            foreach (var tierName in ContextAssembler.TierNames)
                Assert.Contains(capsule.OmittedTiers,
                    e => e.Category == tierName && e.Reason == "unresolved");

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
            var options = DefaultOptions(50, 1);
            var budgetedCapsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(budgetedCapsule);

            // Find an omitted tier with budget_exhausted that is a fetchable tier
            var omittedEntry = budgetedCapsule.OmittedTiers.FirstOrDefault(e => e.Reason == "budget_exhausted"
                && ContextAssembler.TierNames.Contains(e.Category, StringComparer.Ordinal));

            Assert.NotNull(omittedEntry);

            Assert.Contains(budgetedCapsule.InclusionReasons,
                kv => kv.Key == "omittedTiers.budget_exhausted");

            var template = budgetedCapsule.InclusionReasons["omittedTiers.budget_exhausted"];

            // The template must contain the fetch command pattern for fetchable tiers
            Assert.Contains("--mode=context --tier=<category> --symbol=<anchor symbolId>", template);

            // Verify non-fetchable sections appear in the "larger --content-budget" sentence
            if (budgetedCapsule.OmittedTiers.Any(e =>
                    e.Reason == "budget_exhausted" &&
                    !ContextAssembler.TierNames.Contains(e.Category, StringComparer.Ordinal)))
                Assert.Contains("larger --content-budget", template);

            // Fetch the omitted tier unbudgeted via BuildTierPage
            var symbolId = SymbolId.Parse(usedId);
            var page = ContextAssembler.BuildTierPage(
                store, store, snapshotId, symbolId,
                omittedEntry.Category, 1, false,
                0, 100);

            Assert.NotNull(page);
            Assert.Equal(omittedEntry.Category, page.TierName);
            Assert.NotEmpty(page.Items);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     Pins the contract introduced by the T6 framing fix: tier selection must
    ///     not treat a source-less (path-only) item as free, while source-bearing
    ///     items and the anchor pass keep their original estimates.
    /// </summary>
    [Fact]
    public void EstimateTokens_ChargesFramingForPathOnlyItems()
    {
        var pathOnly = new CapsuleItem(
            "T:System.IDisposable|System.Runtime, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
            nameof(IndexedSymbolKind.Type),
            "System.IDisposable",
            "compiler_proved",
            "Implements",
            null);
        Assert.True(ContextAssembler.EstimateTokens(pathOnly) > 0,
            "A source-less (path-only) item must not be estimated as free: tier selection "
            + "would otherwise let a path-heavy tier leapfrog a source-bearing tier.");

        const string source = "public int UsedByCaller(int x) => x;";
        var withSource = new CapsuleItem(
            "M:TestProject.ExternalContract.UsedByCaller(System.Int32)|TestProject",
            nameof(IndexedSymbolKind.Method),
            "TestProject.ExternalContract.UsedByCaller",
            "compiler_proved",
            "Calls",
            source);
        Assert.Equal(source.Length / 4, ContextAssembler.EstimateTokens(withSource));

        // The anchor pass keeps the string overload: a null anchor source stays 0
        // (the anchor carries no JSON framing).
        Assert.Equal(0, ContextAssembler.EstimateTokens((string?)null));
    }

    /// <summary>
    ///     REGRESSION test for the T6 framing fix (commit 286f72a): a path-heavy
    ///     tier must not leapfrog a source-bearing tier at the selection boundary.
    ///     The selection boundary is exercised through <see cref="ContextBudgeter.Apply" />
    ///     directly (the exact code T6 changed at ContextBudgeter.cs:37,50) with a
    ///     source-bearing tier first and a path-only tier second, at a budget that
    ///     admits the source tier plus half the path-only framing charge (40/2). The
    ///     path-only tier fits under the old 0-cost estimate (anchor + source tier + 0)
    ///     but not under the new 40-token-per-item estimate, so pre-T6 it was admitted
    ///     for free and post-T6 it is truncated while the source tier survives.
    ///     This deliberately does not run the full <c>ResolveAndAssemble</c> pipeline:
    ///     CapsuleBudgetEnforcer's retained floor (the omittedTiers.* recovery
    ///     instruction, ~110-145 tokens for a capsule with budget_exhausted tiers)
    ///     exceeds the budgeter's path-tier exclusion window (anchor + source tier +
    ///     40 at most), so at every budget where the path tier is excluded the
    ///     enforcer also clears the source tier — the selection difference T6 makes is
    ///     not observable in the final artifact. The boundary assertion therefore pins
    ///     the selection stage, which is the layer T6 changed.
    /// </summary>
    [Fact]
    public void PathHeavyTier_DoesNotLeapfrogSourceTier_AtSelectionBoundary()
    {
        var anchor = new CapsuleAnchor(
            "T:TestProject.ExternalContract|TestProject",
            "TestProject.ExternalContract",
            nameof(IndexedSymbolKind.Type),
            new string('a', 100)); // anchor cost = 25 tokens

        var sourceItem = new CapsuleItem(
            "M:TestProject.LeapfrogCaller.CallAnchor()|TestProject",
            nameof(IndexedSymbolKind.Method),
            "TestProject.LeapfrogCaller.CallAnchor",
            "compiler_proved",
            "Calls",
            new string('b', 100)); // source tier cost = 25 tokens

        // Path-only item: no source. Pre-T6 this estimated at 0 tokens; post-T6
        // it is charged PathOnlyFramingChars / 4 = 40 tokens.
        var pathItem = new CapsuleItem(
            "T:System.IDisposable|System.Runtime",
            nameof(IndexedSymbolKind.Type),
            "System.IDisposable",
            "compiler_proved",
            "Implements",
            null);

        var capsule = new ContextCapsule(anchor);
        var anchorCost = ContextAssembler.EstimateTokens(anchor.Source);
        var sourceTierCost = ContextAssembler.EstimateTokens(sourceItem);
        // Boundary budget: anchor + source tier + half the path-only framing
        // charge. PathOnlyFramingChars is the deciding factor: the path tier fits
        // at anchor + source tier + 0 (old estimate) but not at + 40.
        var budget = anchorCost + sourceTierCost + 20;

        ContextBudgeter.Apply(
            capsule,
            [
                new FixedTierBuilder("direct_callers", sourceItem),
                new FixedTierBuilder("contracts", pathItem)
            ],
            budget,
            anchorCost);

        // The source-bearing tier (higher priority) is admitted at the boundary...
        Assert.NotEmpty(capsule.DirectCallers);
        Assert.DoesNotContain(capsule.TruncatedCategories, category => category == "direct_callers");
        // ...while the path-heavy tier is NOT: its framing charge exceeds the
        // headroom and the budgeter truncates it. Pre-T6 it was admitted for free
        // (Contracts would contain the path-only item here).
        Assert.Empty(capsule.Contracts);
        Assert.Contains(capsule.TruncatedCategories, category => category == "contracts");
        Assert.Contains(capsule.OmittedTiers,
            entry => entry is { Category: "contracts", Reason: "budget_exhausted" });
    }

    /// <summary>
    ///     A capsule assembled over any symbol in a project that contains pipeline
    ///     behaviors includes an uncertainty entry for the skipped MediatR pattern.
    /// </summary>
    [SkippableFact]
    public async Task Capsule_PipelineBehavior_SurfacesUnmodeledMediatRUncertainty()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Ping.cs"] = """
                              using MediatR;

                              namespace App;
                              public class Ping : IRequest<string> { }
                              public class PingHandler : IRequestHandler<Ping, string>
                              {
                                  public Task<string> Handle(Ping request, CancellationToken ct) => Task.FromResult("pong");
                              }
                              public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
                                  where TRequest : notnull
                              {
                                  public Task<TResponse> Handle(TRequest req, RequestHandlerDelegate<TResponse> next, CancellationToken ct)
                                      => next();
                              }
                              """
            },
            ["MediatR@12.4.1"]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);
        var store = OpenStore(DbPath);

        // Resolve anchor = PingHandler
        var symbols = store.SearchSymbols(snapshotId, "PingHandler");
        Skip.If(symbols.Count == 0, "PingHandler not found in snapshot.");
        var anchor = symbols.First();

        // Assemble capsule
        var lookup = new ContextLookup(snapshotId, anchor.SymbolId, null, null);
        var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 1);
        var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

        Assert.Contains(capsule.Uncertainties, u => u.RelationshipKind == "unmodeled_mediatr_pattern");
    }

    // ── T7 ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     One tier builder that always emits the exact items it was given, so the
    ///     selection boundary can be exercised without an indexed fixture.
    /// </summary>
    private sealed class FixedTierBuilder(string name, params CapsuleItem[] items) : IContextTierBuilder
    {
        public string Name => name;
        public string InclusionReason => $"Synthetic tier for {name}.";

        public List<CapsuleItem> Build()
        {
            return [.. items];
        }
    }
}