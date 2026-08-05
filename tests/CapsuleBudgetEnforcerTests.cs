using System.Text.Json;
using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class CapsuleBudgetEnforcerTests : IDisposable
{
    private const string SnapshotId = "snap-budget-enforcer";

    // Kept-prefix order: highest priority first. surroundingSource is last
    // because the enforcer clears that low-value bulk before any other
    // section, regardless of its position in the tier priority.
    private static readonly string[] PriorityOrder =
    [
        "contracts", "directCallees",
        "uncertainties", "suggestedVerification", "incomingPaths", "outgoingPaths",
        "topology", "constraints", "completeness", "likelyChangeSites",
        "affectedPublicSurfaces", "inclusionReasons", "surroundingSource",
    ];

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"budget_enforcer_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void UnderBudget_SetsEstimateFromEmittedRepresentationWithoutTruncation()
    {
        var capsule = Capsule("public sealed class C { }");
        CapsuleBudgetEnforcer.Enforce(capsule, budget: 100_000, tierPriority: ["contracts", "directCallees"]);

        Assert.False(capsule.Truncated);
        Assert.Empty(capsule.OmittedTiers);
        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);
        Assert.True(capsule.EstimatedTokens <= 100_000);
    }

    [Fact]
    public void AnchorAloneOverBudget_BoundsAnchorSourceToFitAndNeverDropsTheAnchor()
    {
        var capsule = Capsule(new string('x', 40_000));
        CapsuleBudgetEnforcer.Enforce(capsule, budget: 1000, tierPriority: ["contracts"]);

        // The anchor is never dropped, but its source is bounded as the
        // last-resort trim step, so the delivered estimate honors the budget
        // basis --budget is documented to bound.
        Assert.NotEmpty(capsule.Anchor.Source);
        Assert.Contains("source truncated", capsule.Anchor.Source);
        Assert.Contains(capsule.OmittedTiers, entry => entry.Category == "anchor" && entry.Reason == "summarized");
        Assert.True(capsule.Truncated);
        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);
        Assert.True(capsule.EstimatedTokens <= 1000,
            $"Delivered estimate {capsule.EstimatedTokens} exceeds the budget basis 1000.");
    }

    [Fact]
    public void SourceBounding_PreservesDispatchRelationshipQualifier()
    {
        var capsule = Capsule("anchor");
        capsule.DirectCallers.Add(new CapsuleItem(
            "M:Controller.Run|prod", "Method", "Controller.Run", "possible", "Calls",
            new string('x', 2_000), null,
            "Calls → MayDispatchTo; indirect runtime dispatch candidate.",
            CapsuleRelationship.IndirectDispatchCandidate, direct: false));

        // 500 is below the original 2,000-character item's content estimate,
        // but above the bounded representation, so this exercises source
        // trimming rather than whole-tier removal.
        CapsuleBudgetEnforcer.Enforce(capsule, budget: 500, tierPriority: ["directCallers"]);

        var retained = Assert.Single(capsule.DirectCallers);
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, retained.Relationship);
        Assert.False(retained.Direct);
        Assert.Equal("possible", retained.Provenance);
        Assert.Contains("MayDispatchTo", retained.InclusionReason);
        Assert.Contains("source truncated", retained.Source);
    }

    [Fact]
    public void OverBudget_KeepsHighestPriorityPrefixAndRecordsEveryDroppedCategory()
    {
        var capsule = Capsule("a");
        capsule.Contracts.Add(Item("c"));
        capsule.DirectCallees.Add(Item("d"));
        capsule.Uncertainties.Add(new UncertaintyEntry(["s"], "reflection_target_unknown", "uncertain"));
        capsule.SuggestedVerification.Add(new VerificationSuggestion("t", "T", "verify"));
        capsule.LikelyChangeSites.Add(new LikelyChangeSite("x.cs", 0, "anchor", "s"));
        capsule.InclusionReasons["contracts"] = "reason";
        for (var i = 0; i < 200; i++)
            capsule.IncomingPaths.Add(MakePath(10));

        // Sections that actually carried content when the enforcer started,
        // ordered by priority. Greedy dropping can only ever retain a prefix of
        // this sequence.
        var originallyHadContent = PriorityOrder
            .Where(name => HasContent(capsule, name))
            .ToList();

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 2000, tierPriority: ["contracts", "directCallees"]);

        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);
        Assert.True(capsule.Truncated);
        Assert.NotEmpty(capsule.OmittedTiers);

        // Bounded: either the emitted estimate fits the budget, or the only
        // remaining overage is residual content that cannot be trimmed (the
        // anchor is bounded as a last resort, never dropped), which is declared.
        Assert.True(
            capsule.EstimatedTokens <= 2000
            || capsule.OmittedTiers.Any(entry => entry.Category == "anchor" && entry.Reason == "budget_exhausted"));

        // Greedy prefix: sections are dropped lowest-priority first, so every
        // section that originally had content and lies at or below the first
        // dropped section must be content-empty afterwards.
        var retained = new HashSet<string>(
            PriorityOrder.Where(name => HasContent(capsule, name)), StringComparer.Ordinal);
        var keptPrefixLength = 0;
        while (keptPrefixLength < originallyHadContent.Count
               && retained.Contains(originallyHadContent[keptPrefixLength]))
            keptPrefixLength++;
        Assert.All(originallyHadContent.Skip(keptPrefixLength),
            name => Assert.False(retained.Contains(name),
                $"Lower-priority section '{name}' was retained after a higher-priority section was dropped."));

        // Every omitted/summarized category is recorded with a reason.
        Assert.All(capsule.OmittedTiers, entry =>
        {
            Assert.Contains(entry.Category, PriorityOrder.Concat(["anchor"]));
            Assert.True(entry.Reason is "budget_exhausted" or "summarized",
                $"Unexpected omission reason '{entry.Reason}' for '{entry.Category}'.");
        });
    }

    [Fact]
    public void AssembledCapsule_TopologyReferencesPathsInsteadOfDuplicatingThem()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, IncludeCompletenessDetail: false);

        var topology = Assert.IsType<CapsuleTopology>(capsule.Topology);
        Assert.Equal(0, topology.Current.IncomingPathCount);
        Assert.Equal(1, topology.Current.OutgoingPathCount);
        Assert.Equal(1, topology.Current.TotalHopCount);
        Assert.Equal("see incomingPaths", topology.Current.IncomingReference);
        Assert.Equal("see outgoingPaths", topology.Current.OutgoingReference);

        // The serialized topology section carries only the reference summary,
        // never the full hop data; the hop data lives once under outgoingPaths.
        var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(capsule));
        var topologyCurrent = document.RootElement.GetProperty("topology").GetProperty("current");
        Assert.False(topologyCurrent.TryGetProperty("Hops", out _));
        Assert.True(document.RootElement.GetProperty("outgoingPaths")
            .EnumerateArray().First().TryGetProperty("Hops", out _));
    }

    [Fact]
    public void AssembledCapsule_CompletenessIsReasonProjectSummaryByDefault()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, IncludeCompletenessDetail: false);

        Assert.NotNull(capsule.Completeness);
        // Per-document rows are suppressed by default.
        Assert.Empty(capsule.Completeness.BindingIncompleteness);
        Assert.Equal(16, capsule.Completeness.BindingIncompletenessTotal);
        Assert.Collection(capsule.Completeness.BindingIncompletenessSummary,
            entry =>
            {
                Assert.Equal("App", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.CompilerError, entry.Reason);
                Assert.Equal(8, entry.Count);
            },
            entry =>
            {
                Assert.Equal("App", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.UnresolvedMetadata, entry.Reason);
                Assert.Equal(5, entry.Count);
            },
            entry =>
            {
                Assert.Equal("Tests", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.CompilerError, entry.Reason);
                Assert.Equal(3, entry.Count);
            });
    }

    [Fact]
    public void AssembledCapsule_CompletenessDetailOptionRetainsDetailedRows()
    {
        var store = CreateSeededStore();
        var capsule = Assemble(store, IncludeCompletenessDetail: true);

        Assert.NotNull(capsule.Completeness);
        Assert.Equal(4, capsule.Completeness.BindingIncompleteness.Count);
        Assert.NotEmpty(capsule.Completeness.BindingIncompletenessSummary);
        Assert.Equal(16, capsule.Completeness.BindingIncompletenessTotal);
    }

    [Fact]
    public void CompletenessSummary_GroupsRecordsByProjectAndReasonDeterministically()
    {
        var records = new List<BindingIncompletenessRecord>
        {
            new("App", "A.cs", BindingIncompletenessReason.CompilerError, 3, "v1"),
            new("App", "A.cs", BindingIncompletenessReason.UnresolvedMetadata, 2, "v1"),
            new("App", "B.cs", BindingIncompletenessReason.CompilerError, 5, "v1"),
            new("Tests", "T.cs", BindingIncompletenessReason.CompilerError, 1, "v1"),
        };

        var summary = SnapshotCompleteness.BuildBindingIncompletenessSummary(records);

        Assert.Collection(summary,
            entry =>
            {
                Assert.Equal("App", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.CompilerError, entry.Reason);
                Assert.Equal(8, entry.Count);
            },
            entry =>
            {
                Assert.Equal("App", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.UnresolvedMetadata, entry.Reason);
                Assert.Equal(2, entry.Count);
            },
            entry =>
            {
                Assert.Equal("Tests", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.CompilerError, entry.Reason);
                Assert.Equal(1, entry.Count);
            });
    }

    [Fact]
    public void TypeAnchorAtTightBudget_KeepsRequiredSectionsWithBoundedSource()
    {
        // Reproduces the eNote CourseService type-anchor shape: a large anchor
        // source, a contract, member-level callees and dispatch targets, and
        // surrounding member source that heavily overlaps the anchor (a
        // primary-constructor member sharing the type's declaration span).
        var capsule = Capsule(new string('x', 5071));
        capsule.Contracts.Add(Item(new string('i', 796)));
        for (var i = 0; i < 6; i++)
            capsule.DirectCallees.Add(Item($"    Task Foo{i}Async(int id);\r\n", $"callee{i}"));
        for (var i = 0; i < 7; i++)
            capsule.RegisteredImplementations.Add(Item($"    Task Bar{i}Async(int id);\r\n", $"reg{i}"));
        capsule.SurroundingSource.Add(Item(new string('x', 5071), "ctor")); // duplicates the anchor
        for (var i = 0; i < 7; i++)
            capsule.SurroundingSource.Add(Item($"    Task Method{i}(int id)\r\n    {{\r\n        return Task.FromResult(id);\r\n    }}\r\n", $"surr{i}"));
        capsule.Uncertainties.Add(new UncertaintyEntry(["s"], "binding_incompleteness",
            "190 binding(s) could not be completed because the snapshot compilation reported compiler errors."));
        for (var i = 0; i < 50; i++)
            capsule.OutgoingPaths.Add(MakePath(8));
        capsule.LikelyChangeSites.Add(new LikelyChangeSite("x.cs", 0, "anchor", "s"));
        capsule.AffectedPublicSurfaces.Add(Item("public sealed class C", "surface"));
        capsule.InclusionReasons["contracts"] = "reason";

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 4000, tierPriority:
            ["contracts", "directCallees", "registeredImplementations", "surroundingSource"]);

        Assert.NotEmpty(capsule.Anchor.Source);
        Assert.NotEmpty(capsule.Contracts);
        Assert.NotEmpty(capsule.DirectCallees);
        Assert.NotEmpty(capsule.RegisteredImplementations);
        Assert.NotEmpty(capsule.SurroundingSource);
        Assert.NotEmpty(capsule.Uncertainties);
        Assert.True(CapsuleBudgetEnforcer.Measure(capsule) <= 4000);
        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);

        // Bounded source is recorded honestly as summarization; whole sections
        // are only cleared when bounding cannot fit.
        Assert.Contains(capsule.OmittedTiers, entry => entry.Reason == "summarized");
        Assert.All(capsule.SurroundingSource, item =>
            Assert.True(item.Source == null || item.Source.Length <= 800 + "\n// … source truncated by token budget …".Length));
    }

    // Audit finding E shape: a bulk anchor, many retained surroundingSource
    // entries, paths, and the small high-signal sections, requested at a
    // budget the unbounded capsule heavily overshoots. The delivered
    // estimatedTokens is the budget basis and must come back within budget.
    [Fact]
    public void AuditShapedCapsule_DeliversEstimateWithinBudgetOnTheStatedBasis()
    {
        var capsule = Capsule(new string('x', 6_000));
        for (var i = 0; i < 14; i++)
            capsule.SurroundingSource.Add(Item(new string('s', 2_000), $"surr{i}"));
        for (var i = 0; i < 50; i++)
            capsule.OutgoingPaths.Add(MakePath(8));
        capsule.AffectedPublicSurfaces.Add(Item("public sealed class C", "surface"));
        capsule.InclusionReasons["contracts"] = "reason";

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 2000, tierPriority:
            ["contracts", "directCallees", "registeredImplementations", "surroundingSource"]);

        Assert.True(capsule.EstimatedTokens <= 2000,
            $"Delivered estimate {capsule.EstimatedTokens} exceeds the budget basis 2000.");
        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);
    }

    // When the budget forces drops, the low-value surroundingSource bulk is
    // dropped before the small high-signal sections: inclusionReasons and
    // affectedPublicSurfaces must survive it.
    [Fact]
    public void ForcedDrops_InclusionReasonsAndAffectedPublicSurfacesSurviveSurroundingSource()
    {
        var capsule = Capsule("a");
        for (var i = 0; i < 4; i++)
            capsule.SurroundingSource.Add(Item(new string('s', 2_000), $"surr{i}"));
        capsule.AffectedPublicSurfaces.Add(Item("public sealed class C", "surface"));
        capsule.InclusionReasons["contracts"] = "reason";

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 500, tierPriority:
            ["contracts", "surroundingSource"]);

        Assert.Empty(capsule.SurroundingSource);
        Assert.Contains(capsule.OmittedTiers,
            entry => entry.Category == "surroundingSource" && entry.Reason == "budget_exhausted");

        Assert.NotEmpty(capsule.AffectedPublicSurfaces);
        Assert.Contains("contracts", capsule.InclusionReasons.Keys);
        Assert.DoesNotContain(capsule.OmittedTiers,
            entry => entry.Category == "inclusionReasons" || entry.Category == "affectedPublicSurfaces");
        Assert.True(capsule.EstimatedTokens <= 500);
    }

    // estimatedTokens is the content basis the budget bounds; estimatedArtifactTokens
    // is the whole serialized file. Conflating them under-budgets a consumer's
    // context window, so both must be reported and each must mean what it says.
    [Fact]
    public void SettledCapsule_ReportsContentEstimateAndWholeArtifactEstimateSeparately()
    {
        var capsule = Capsule("public sealed class C { }");
        for (var i = 0; i < 20; i++)
            capsule.DirectCallers.Add(new CapsuleItem(
                $"M:Caller{i}.Run|prod", "Method", $"Namespace.Caller{i}.Run", "compiler_proved", "Calls",
                $"void Run{i}() {{ }}", null, "Direct compiler-resolved call.",
                CapsuleRelationship.DirectCaller, direct: true));

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 100_000, tierPriority: ["directCallers"]);

        Assert.Equal(CapsuleBudgetEnforcer.Measure(capsule), capsule.EstimatedTokens);

        // The artifact estimate is part of the document it measures, so it is
        // settled to within one token of the emitted file's own length/4.
        var emitted = ContextCapsuleJson.Serialize(capsule);
        Assert.InRange(capsule.EstimatedArtifactTokens, emitted.Length / 4 - 1, emitted.Length / 4 + 1);

        // The framing this capsule carries (symbol ids, FQNs, edge kinds,
        // provenance) is exactly what the content basis excludes.
        Assert.True(capsule.EstimatedArtifactTokens > capsule.EstimatedTokens);
    }

    // The two figures answer different questions and a JSON-only consumer (an
    // MCP tool result, no --help or summary line) must not have to know the
    // distinction from prose. The tokenEstimate advisory restates both under
    // role-named fields and is kept in sync by construction, so a consumer
    // reading it cannot mistake the budget basis for the delivery size.
    [Fact]
    public void SettledCapsule_EmitsTokenEstimateAdvisoryRestatingBothFiguresUnderRoleNames()
    {
        var capsule = Capsule("public sealed class C { }");
        for (var i = 0; i < 20; i++)
            capsule.DirectCallers.Add(new CapsuleItem(
                $"M:Caller{i}.Run|prod", "Method", $"Namespace.Caller{i}.Run", "compiler_proved", "Calls",
                $"void Run{i}() {{ }}", null, "Direct compiler-resolved call.",
                CapsuleRelationship.DirectCaller, direct: true));

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 100_000, tierPriority: ["directCallers"]);

        var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(capsule));
        var advisory = document.RootElement.GetProperty("tokenEstimate");

        // The advisory restates the two integer fields under role names, never
        // duplicates a third independent figure: drift is impossible by design.
        Assert.Equal(capsule.EstimatedTokens, advisory.GetProperty("budgetBasis").GetInt32());
        Assert.Equal(capsule.EstimatedArtifactTokens, advisory.GetProperty("delivery").GetInt32());

        // The advisory points the consumer at the field to size from, and
        // declares what the budget basis measures, inline in the payload.
        Assert.Equal("delivery", advisory.GetProperty("windowSizingField").GetString());
        Assert.Contains("identity/provenance framing excluded", advisory.GetProperty("basis").GetString());
    }

    [Fact]
    public void RepeatedTrimOfOneCategory_LeavesExactlyOneTerminalRecord()
    {
        var capsule = Capsule("a");
        for (var i = 0; i < 200; i++)
            capsule.IncomingPaths.Add(MakePath(10));

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 200, tierPriority: ["contracts"]);

        // incomingPaths is bounded first ("summarized") and cleared later
        // ("budget_exhausted") under this pressure. Only the terminal state is
        // reported: a consumer reads the record, not a chronology.
        Assert.All(
            capsule.OmittedTiers.GroupBy(entry => entry.Category, StringComparer.Ordinal),
            group => Assert.Single(group));
        var record = Assert.Single(capsule.OmittedTiers, entry => entry.Category == "incomingPaths");
        Assert.Equal("budget_exhausted", record.Reason);
        Assert.Empty(capsule.IncomingPaths);
    }

    // A partially included tier is recorded budget_exhausted while still carrying
    // items. That is the documented "complete greedy prefix" shape, and it must
    // stay distinguishable from a fully omitted tier by item count alone.
    [Fact]
    public void PartiallyIncludedTier_IsDistinguishableFromAFullyOmittedOne()
    {
        var capsule = Capsule("a");
        capsule.Contracts.Add(Item(new string('c', 4_000), "contract"));
        capsule.DirectCallees.Add(Item(new string('d', 4_000), "callee"));
        capsule.OmittedTiers.Add(new TruncationEntry("relevantTests", "budget_exhausted"));

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 400,
            tierPriority: ["contracts", "directCallees", "relevantTests"]);

        var partial = Assert.Single(capsule.OmittedTiers, entry => entry.Category == "contracts");
        Assert.Equal("summarized", partial.Reason);
        Assert.NotEmpty(capsule.Contracts);

        var omitted = Assert.Single(capsule.OmittedTiers, entry => entry.Category == "relevantTests");
        Assert.Equal("budget_exhausted", omitted.Reason);
        Assert.Empty(capsule.RelevantTests);
    }

    // The capsule that omits the most is the one that most needs to say how to
    // recover the omissions, so the omittedTiers.* meta-entries outlive the
    // pressure that creates them : even when every other section is cleared.
    [Fact]
    public void UnderFullPressure_OmissionRecoveryHintsSurviveWhileOtherReasonsAreCleared()
    {
        var capsule = Capsule(new string('x', 40_000));
        capsule.Contracts.Add(Item("c"));
        capsule.InclusionReasons["contracts"] = "Compiler-resolved contracts implemented by the anchor.";
        capsule.InclusionReasons["omittedTiers.budget_exhausted"] =
            "Fetch an omitted tier on its own, unbudgeted: --mode=context --tier=<category> --symbol=<anchor symbolId>.";
        capsule.InclusionReasons["omittedTiers.unresolved"] =
            "An omitted tier marked 'unresolved' is not evidence that no such relation exists.";

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 100, tierPriority: ["contracts"]);

        Assert.Contains(capsule.OmittedTiers, entry => entry.Category == "anchor");
        Assert.DoesNotContain("contracts", capsule.InclusionReasons.Keys);
        Assert.Contains("omittedTiers.budget_exhausted", capsule.InclusionReasons.Keys);
        Assert.Contains("omittedTiers.unresolved", capsule.InclusionReasons.Keys);
    }

    // Zeroed topology counts beside a populated caller tier read as "no incoming
    // references" : a claim the capsule never established. Dropped, not zeroed.
    [Fact]
    public void DroppedTopology_IsOmittedFromTheArtifactRatherThanSerializedAsZeroCounts()
    {
        var capsule = Capsule("a");
        capsule.Topology = new CapsuleTopology(
            new CapsuleTopologyReference("see incomingPaths", "see outgoingPaths", 4, 2, 18), [], []);
        for (var i = 0; i < 200; i++)
            capsule.IncomingPaths.Add(MakePath(10));

        CapsuleBudgetEnforcer.Enforce(capsule, budget: 100, tierPriority: ["contracts"]);

        Assert.Null(capsule.Topology);
        Assert.Contains(capsule.OmittedTiers,
            entry => entry.Category == "topology" && entry.Reason == "budget_exhausted");
        var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(capsule));
        Assert.False(document.RootElement.TryGetProperty("topology", out _));
    }

    private static ContextCapsule Capsule(string anchorSource)
        => new(new CapsuleAnchor("T:C|prod", "C", "Type", anchorSource));

    private static CapsuleItem Item(string source, string id = "item")
        => new(id, "Type", id, "compiler_proved", "test", source);

    private static ImpactPath MakePath(int hopCount)
        => new(Enumerable.Range(0, hopCount)
            .Select(i => new ImpactHop($"S{i}", $"T{i}", "Calls", "compiler_proved", "doc.cs", i, 1, i, 2))
            .ToList());

    private static bool HasContent(ContextCapsule capsule, string name)
        => name switch
        {
            "contracts" => capsule.Contracts.Count > 0,
            "directCallees" => capsule.DirectCallees.Count > 0,
            "directCallers" => capsule.DirectCallers.Count > 0,
            "registeredImplementations" => capsule.RegisteredImplementations.Count > 0,
            "relevantTests" => capsule.RelevantTests.Count > 0,
            "secondDegreeContext" => capsule.SecondDegreeContext.Count > 0,
            "surroundingSource" => capsule.SurroundingSource.Count > 0,
            "incomingPaths" => capsule.IncomingPaths.Count > 0,
            "outgoingPaths" => capsule.OutgoingPaths.Count > 0,
            "topology" => capsule.Topology != null
                && (capsule.Topology.Current != CapsuleTopologyReference.Empty
                    || capsule.Topology.Target.Count > 0
                    || capsule.Topology.Annotations.Count > 0),
            "constraints" => capsule.Constraints.Count > 0,
            "completeness" => capsule.Completeness != null,
            "uncertainties" => capsule.Uncertainties.Count > 0,
            "suggestedVerification" => capsule.SuggestedVerification.Count > 0,
            "likelyChangeSites" => capsule.LikelyChangeSites.Count > 0,
            "affectedPublicSurfaces" => capsule.AffectedPublicSurfaces.Count > 0,
            "inclusionReasons" => capsule.InclusionReasons.Count > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown section."),
        };

    private ContextCapsule Assemble(SqliteIndexStore store, bool IncludeCompletenessDetail)
        => new ContextAssembler
        {
            EdgeStore = store,
            DeclarationStore = store,
            BindingIncompletenessStore = store,
            SnapshotId = SnapshotId,
            SymbolId = SymbolId.Parse("T:MyApp.Service|prod"),
            Intent = ContextIntent.Inspect,
            Budget = 100_000,
            MaxHops = 3,
            IncludeGenerated = false,
            IncludeCompletenessDetail = IncludeCompletenessDetail,
        }.Assemble();

    private SqliteIndexStore CreateSeededStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        SeedFkReferences();
        _store.SaveDeclarations(SnapshotId,
        [
            new SymbolDeclaration
            {
                SymbolId = SymbolId.Parse("T:MyApp.Service|prod"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-v-budget-enforcer",
                FullSpan = new DeclarationSpan(null, null),
                SignatureSpan = new DeclarationSpan(null, null),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(null, null),
                MetadataJson = """{"accessibility":"Public"}""",
            },
        ]);
        _store.SaveEdges(SnapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "T:MyApp.Service|prod",
                TargetSymbolId = "T:MyApp.IService|prod",
                Kind = EdgeKind.Implements.ToString(),
                Provenance = Provenance.CompilerProved,
                SnapshotId = SnapshotId,
                ExtractorVersion = "v1",
            },
        ]);
        _store.SaveBindingIncompleteness(SnapshotId,
        [
            new BindingIncompletenessRecord("App", "A.cs", BindingIncompletenessReason.CompilerError, 3, "v1"),
            new BindingIncompletenessRecord("App", "A.cs", BindingIncompletenessReason.UnresolvedMetadata, 5, "v1"),
            new BindingIncompletenessRecord("App", "B.cs", BindingIncompletenessReason.CompilerError, 5, "v1"),
            new BindingIncompletenessRecord("Tests", "T.cs", BindingIncompletenessReason.CompilerError, 3, "v1"),
        ]);
        return _store;
    }

    private void SeedFkReferences()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-budget-enforcer', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-budget-enforcer', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-budget-enforcer', 'test.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc-v-budget-enforcer', 'doc-budget-enforcer', 'hash');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        cmd.ExecuteNonQuery();
    }
}
