using System.Globalization;
using System.Text;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class CapsuleCharacterizationTests : IntegrationTestBase
{
    private static readonly string[] PagedCallers = ["CallerA.A", "CallerB.B", "CallerC.C"];

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

    [Fact]
    public async Task Capsule_RespectsContentBudget_Generous()
    {
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

    [Fact]
    public async Task Capsule_RespectsContentBudget_Mid()
    {
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

    [Fact]
    public async Task Capsule_RespectsContentBudget_SmallButAboveFloor()
    {
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, usedId, null, null);
            // 160 tokens = 480 chars at CharsPerToken 3: the same character budget
            // as 120 tokens at the former divisor 4, just above the retained floor.
            var options = DefaultOptions(160, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            Assert.True(capsule.EstimatedTokens <= 160,
                $"EstimatedTokens ({capsule.EstimatedTokens}) exceeded small budget (160).");
            Assert.True(capsule.Truncated, "Capsule should be truncated at small budget.");
            Assert.DoesNotContain(capsule.OmittedTiers,
                e => e is { Category: "anchor", Reason: "budget_exhausted" });
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_RespectsContentBudget_FloorAnchorBudgetExhausted()
    {
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

    [Fact]
    public async Task Capsule_EmptyTier_MeansProvedAbsence()
    {
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
            // InclusionReasons must NOT contain omitted_tiers_unresolved
            Assert.False(capsule.InclusionReasons.ContainsKey("omitted_tiers_unresolved"),
                "Proved-absence capsule must not carry the 'unresolved' inclusion reason.");
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_EmptyTier_StandaloneClassNoCallers()
    {
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
            Assert.False(capsule.InclusionReasons.ContainsKey("omitted_tiers_unresolved"));
        }
        finally
        {
            store.Close();
        }
    }

    // ── D4 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capsule_EmptyTier_UsesTheProjectsThatCanCallTheAnchor()
    {
        CreateProject("Core", new Dictionary<string, string>
        {
            ["Good.cs"] = "namespace Core; class Owner { private void Unused() { } }",
            ["Second.cs"] = "namespace Core; class Second { }"
        }, msbuildProperties: new Dictionary<string, string> { ["AssemblyName"] = "CoreAssembly" });
        CreateProject("Client", new Dictionary<string, string> { ["Client.cs"] = "class Client { }" },
            projectReferences: ["Core"]);
        CreateProject("Tests", new Dictionary<string, string> { ["Tests.cs"] = "class Tests { }" },
            projectReferences: ["Client"]);
        CreateProject("Other", new Dictionary<string, string> { ["Other.cs"] = "class Other { }" });
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var anchor = ResolveSymbolId(snapshot, "global::Core.Owner.Unused");
        using var store = OpenStore(DbPath);

        Check([], "empty", "empty");
        Check([Loss("Core", "src/Core/Second.cs")], "unresolved", "empty");
        Check([Loss("Client", "src/Client/Client.cs")], "unresolved", "empty");
        Check([Loss("Tests", "src/Tests/Tests.cs")], "unresolved", "empty");
        Check([Loss("Other", "src/Other/Other.cs")], "empty", "empty");
        Check([Loss("Other", "src/Other/Other.cs")], "unresolved", "empty", hasProjectFacts: false);
        Check([Loss("Client", null, BindingIncompletenessReason.ProjectUnreadable)], "unresolved", "empty");
        Check([Loss("Core", "src/Core/Good.cs", BindingIncompletenessReason.ConventionScan)], "empty", "empty");
        Check([Loss("Core", @"src\Core\Good.cs")], "unresolved", "unresolved");
        // A project-wide loss without project facts cannot be mapped to documents, so no scope can prove absence.
        Check([Loss("Core", null)], "unresolved", "unresolved", hasProjectFacts: false);
        Check([Loss("Core", null)], "unresolved", "unresolved");
        Check([Loss("Other", null)], "empty", "empty");

        // Assembly names are case-insensitive. A row whose stored name differs only in case still owns the anchor.
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET assembly_name = 'coreassembly' WHERE snapshot_id = $snapshot AND name = 'Core';";
            command.Parameters.AddWithValue("$snapshot", snapshot);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Check([Loss("Other", "src/Other/Other.cs")], "empty", "empty");
        Check([Loss("Client", "src/Client/Client.cs")], "unresolved", "empty");

        static BindingIncompletenessRecord Loss(string project, string? path,
            string reason = BindingIncompletenessReason.CompilerError) => new(project, path, reason, 1, "0.0.0");

        void Check(List<BindingIncompletenessRecord> records, string upstream, string downstream, bool hasProjectFacts = true)
        {
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, new ContextLookup(snapshot, anchor, null, null),
                DefaultOptions(50_000, 2), new CapsuleBindingReader(records), hasProjectFacts ? store : null);
            foreach (var tier in new[] { "direct_callers", "second_degree_context", "relevant_tests", "registered_implementations", "affected_public_surfaces" })
                Assert.Equal(upstream, Assert.Single(capsule.OmittedTiers, entry => entry.Category == tier).Reason);
            foreach (var tier in new[] { "contracts", "direct_callees", "surrounding_source" })
                Assert.Equal(downstream, Assert.Single(capsule.OmittedTiers, entry => entry.Category == tier).Reason);
            Assert.Equal(upstream == "unresolved" || downstream == "unresolved",
                capsule.InclusionReasons.ContainsKey("omitted_tiers_unresolved"));
            if (capsule.InclusionReasons.TryGetValue("omitted_tiers_unresolved", out var explanation))
            {
                // The private anchor needs no friend facts, so the explanation must not name them.
                Assert.Contains("Only tiers marked 'empty' are a proved absence.", explanation);
                Assert.DoesNotContain("friend", explanation);
            }
        }
    }

    private sealed class CapsuleBindingReader(List<BindingIncompletenessRecord> records) : IBindingIncompletenessStore
    {
        public List<BindingIncompletenessRecord> GetBindingIncompleteness(string snapshotId, string? projectName) => records;
        public void SaveBindingIncompleteness(string snapshotId, IEnumerable<BindingIncompletenessRecord> records) =>
            throw new NotSupportedException();
        public void CopyBindingIncompleteness(string fromSnapshotId, string toSnapshotId) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Capsule_UnresolvedTier_WhenAnchorRegionHasLostBindings()
    {
        var (snapshotId, unusedId, _, _, _, anchorFile) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            // Seed a binding_incompleteness row for the anchor's document.
            // The project_unreadable row covers this anchor's document.
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
            // InclusionReasons must contain omitted_tiers_unresolved
            Assert.True(capsule.InclusionReasons.ContainsKey("omitted_tiers_unresolved"));
            Assert.Contains("unresolved", capsule.InclusionReasons["omitted_tiers_unresolved"]);
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_UnresolvedTier_NullBindingStoreTreatedAsUnobservable()
    {
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
            Assert.True(capsule.InclusionReasons.ContainsKey("omitted_tiers_unresolved"));
        }
        finally
        {
            store.Close();
        }
    }

    // ── D5 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capsule_GapAnchor_MarksEveryTierUnresolved()
    {
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

            Assert.True(capsule.InclusionReasons.ContainsKey("omitted_tiers_unresolved"));
            Assert.Contains("unresolved", capsule.InclusionReasons["omitted_tiers_unresolved"]);

            // Must have a location_gap uncertainty
            Assert.Contains(capsule.Uncertainties, u => u.RelationshipKind == "location_gap");
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_GapAnchor_KeepsItsUncertaintyUnderAnyBudget()
    {
        var (snapshotId, _, _, _, _, _) = await IndexFixtureAsync();

        using var store = OpenStore(DbPath);
        try
        {
            // Anchor.cs line 28 is a comment
            var lookup = new ContextLookup(snapshotId, null, "src/TestProject/Anchor.cs", 28);
            var options = DefaultOptions(1, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule);
            var uncertainty = Assert.Single(capsule.Uncertainties);
            Assert.Equal("location_gap", uncertainty.RelationshipKind);
            Assert.DoesNotContain(capsule.OmittedTiers, e => e.Category == "uncertainties");
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task TierPriority_FollowsD64()
    {
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();
        var anchorTypeId = ResolveSymbolId(snapshotId, "global::TestProject.Anchor");

        using var store = OpenStore(DbPath);
        try
        {
            var inspect = ContextAssembler.ResolveAndAssemble(
                store, store, new ContextLookup(snapshotId, usedId, null, null),
                new ContextAssemblyOptions(ContextIntent.Inspect, 1, 1), store, store);
            Assert.Equal(
                ["contracts", "direct_callees", "direct_callers", "registered_implementations", "relevant_tests", "second_degree_context", "surrounding_source"],
                inspect.OmittedTiers.Take(7).Select(e => e.Category));

            var modify = ContextAssembler.ResolveAndAssemble(
                store, store, new ContextLookup(snapshotId, usedId, null, null),
                new ContextAssemblyOptions(ContextIntent.Modify, 1, 1), store, store);
            Assert.Equal(
                ["contracts", "direct_callers", "registered_implementations", "relevant_tests", "direct_callees", "second_degree_context", "surrounding_source"],
                modify.OmittedTiers.Take(7).Select(e => e.Category));

            var diagnose = ContextAssembler.ResolveAndAssemble(
                store, store, new ContextLookup(snapshotId, usedId, null, null),
                new ContextAssemblyOptions(ContextIntent.Diagnose, 1, 1), store, store);
            Assert.Equal(
                ["direct_callers", "registered_implementations", "contracts", "direct_callees", "relevant_tests", "second_degree_context", "surrounding_source"],
                diagnose.OmittedTiers.Take(7).Select(e => e.Category));

            var typeInspect = ContextAssembler.ResolveAndAssemble(
                store, store, new ContextLookup(snapshotId, anchorTypeId, null, null),
                new ContextAssemblyOptions(ContextIntent.Inspect, 1, 1), store, store);
            Assert.Equal(
                ["contracts", "direct_callees", "direct_callers", "registered_implementations", "surrounding_source", "second_degree_context", "relevant_tests"],
                typeInspect.OmittedTiers.Take(7).Select(e => e.Category));
        }
        finally
        {
            store.Close();
        }
    }

    // ── D6 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capsule_OmittedTier_FetchCommandReturnsTheOmittedContent()
    {
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Pager.cs"] = """
                           namespace TestProject;

                           public class Target { public void Hit() { } }
                           public class CallerA { public void A() => new Target().Hit(); }
                           public class CallerB { public void B() => new Target().Hit(); }
                           public class CallerC { public void C() => new Target().Hit(); }
                           """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var targetId = ResolveSymbolId(snapshotId, "global::TestProject.Target.Hit");
        var expectedIds = PagedCallers
            .Select(name => ResolveSymbolId(snapshotId, $"global::TestProject.{name}"))
            .ToList();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, targetId, null, null);
            var budgetedCapsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, DefaultOptions(1, 1), store, store);

            Assert.Contains(budgetedCapsule.OmittedTiers,
                e => e is { Category: "direct_callers", Reason: "budget_exhausted" });

            Assert.Contains(budgetedCapsule.InclusionReasons,
                kv => kv.Key == "omitted_tiers_budget_exhausted");

            var template = budgetedCapsule.InclusionReasons["omitted_tiers_budget_exhausted"];
            Assert.Contains("--mode=context --tier=<category> --symbol=<anchor symbolId>", template);

            var hasNonFetchable = budgetedCapsule.OmittedTiers.Any(e =>
                e.Reason == "budget_exhausted" &&
                !ContextAssembler.TierNames.Contains(e.Category, StringComparer.Ordinal));
            if (hasNonFetchable)
                Assert.Contains("larger --content-budget", template);
            else
                Assert.DoesNotContain("larger --content-budget", template);

            var page = ContextAssembler.BuildTierPage(
                store, store, snapshotId, SymbolId.Parse(targetId),
                "direct_callers", 1, false,
                0, 100);

            Assert.Equal("direct_callers", page.TierName);
            Assert.Equal(expectedIds.Count, page.TotalItems);
            Assert.Equal(expectedIds, page.Items.Select(i => i.SymbolId));
            Assert.Equal(
                expectedIds.Take(budgetedCapsule.DirectCallers.Count),
                budgetedCapsule.DirectCallers.Select(i => i.SymbolId));
        }
        finally
        {
            store.Close();
        }
    }

    // ── R152 B ──────────────────────────────────────────────────────────

    // R152: the CLI tier page is a CLI response, so it prints every key; a key with
    // no value is null. A last page has no next_cursor and must still print it.
    [Fact]
    public async Task Cli_TierPage_LastPage_PrintsNextCursorAsNull()
    {
        var (_, _, usedId, _, _, _) = await IndexFixtureAsync();

        var json = await CliModeRunner.RunAsync("context", [
            $"--symbol={usedId}", "--tier=direct_callers", "--tier-limit=100",
            "--output=json", $"--output-dir={TestDir}"]);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.TryGetProperty("next_cursor", out var nextCursor));
            Assert.Equal(JsonValueKind.Null, nextCursor.ValueKind);
        }

        var jsonl = await CliModeRunner.RunAsync("context", [
            $"--symbol={usedId}", "--tier=direct_callers", "--tier-limit=100",
            "--output=jsonl", $"--output-dir={TestDir}"]);
        var metaLine = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd('\r');
        using var metaDoc = JsonDocument.Parse(metaLine);
        var meta = metaDoc.RootElement.GetProperty("meta");
        Assert.True(meta.TryGetProperty("next_cursor", out var metaCursor));
        Assert.Equal(JsonValueKind.Null, metaCursor.ValueKind);
    }

    // The shape tests only pin the JSON keys, so this is the one place the CLI cursor is
    // followed to the end: each continuation must resume where the previous page stopped.
    [Fact]
    public async Task Cli_TierPage_Cursor_WalksEveryItemOnce()
    {
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Pager.cs"] = """
                           namespace TestProject;

                           public class Target { public void Hit() { } }
                           public class CallerA { public void A() => new Target().Hit(); }
                           public class CallerB { public void B() => new Target().Hit(); }
                           public class CallerC { public void C() => new Target().Hit(); }
                           """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var targetId = ResolveSymbolId(snapshotId, "global::TestProject.Target.Hit");

        var offsets = new List<int>();
        var itemsSeen = new List<string>();
        string? cursor = null;
        do
        {
            var args = new List<string>
            {
                $"--symbol={targetId}", "--tier=direct_callers", "--tier-limit=1",
                "--output=json", $"--output-dir={TestDir}"
            };
            if (cursor != null)
                args.Add($"--cursor={cursor}");

            using var doc = JsonDocument.Parse(await CliModeRunner.RunAsync("context", args.ToArray()));
            var root = doc.RootElement;
            Assert.Equal(3, root.GetProperty("total_items").GetInt32());
            offsets.Add(root.GetProperty("offset").GetInt32());
            var items = root.GetProperty("items");
            Assert.Equal(1, items.GetArrayLength());
            itemsSeen.Add(items[0].GetProperty("symbol_id").GetString()!);
            cursor = root.GetProperty("next_cursor").GetString();
        } while (cursor != null && offsets.Count < 10);

        Assert.Equal([0, 1, 2], offsets);
        Assert.Equal(
            PagedCallers.Select(name => ResolveSymbolId(snapshotId, $"global::TestProject.{name}")),
            itemsSeen);
        Assert.Null(cursor);
    }

    // ── A1 ──────────────────────────────────────────────────────────────

    private const string TestTargetSource = """
                                            namespace TestProject;

                                            public class TestTarget
                                            {
                                                public int Run(int x) => x;
                                            }
                                            """;

    private static string ManyTestMethodsSource()
    {
        var source = new StringBuilder();
        source.AppendLine("using Xunit;");
        source.AppendLine();
        source.AppendLine("public class TestTargetTests");
        source.AppendLine("{");
        for (var i = 0; i < 12; i++)
        {
            source.AppendLine("    [Fact]");
            source.AppendLine(CultureInfo.InvariantCulture, $"    public void Run_{i}() => new TestProject.TestTarget().Run({i});");
        }

        source.AppendLine("}");
        return source.ToString();
    }

    private async Task<(string SnapshotId, string AnchorId)> IndexTestedAnchorAsync()
    {
        CreateProject("TestProject", new Dictionary<string, string> { ["TestTarget.cs"] = TestTargetSource });
        CreateProject("TestProject.Tests",
            new Dictionary<string, string> { ["TestTargetTests.cs"] = ManyTestMethodsSource() },
            projectReferences: ["TestProject"],
            packageReferences: ["xunit@2.9.3"]);
        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);
        var anchorId = ResolveSymbolId(snapshotId, "global::TestProject.TestTarget.Run");
        return (snapshotId, anchorId);
    }

    [Fact]
    public async Task Capsule_TestedByManyTests_GroupsByClassAndArtifactEstimateMatchesFile()
    {
        var (snapshotId, anchorId) = await IndexTestedAnchorAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            var suggestion = Assert.Single(capsule.SuggestedVerification);
            Assert.Contains("FullyQualifiedName~TestTargetTests", suggestion.Command);

            var serialized = ContextCapsuleJson.Serialize(capsule);
            Assert.Equal(serialized.Length / 3, capsule.EstimatedArtifactTokens);
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_TestedByManyTests_VerificationIsBudgetGoverned()
    {
        var (snapshotId, anchorId) = await IndexTestedAnchorAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 1, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.Empty(capsule.SuggestedVerification);
            Assert.Contains(capsule.OmittedTiers,
                entry => entry is { Category: "suggested_verification", Reason: "budget_exhausted" });
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_Verification_NamesTheIndexedProject()
    {
        var (snapshotId, anchorId) = await IndexTestedAnchorAsync();
        File.WriteAllText(Path.Combine(TestDir, "src", "TestProject.Tests", "Aaa.csproj"), "");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            var suggestion = Assert.Single(capsule.SuggestedVerification);
            Assert.Contains("\"src/TestProject.Tests/TestProject.Tests.csproj\"", suggestion.Command);
        }
        finally
        {
            store.Close();
        }

        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE projects SET project_path = NULL WHERE snapshot_id = $snapshot;";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            Assert.Equal(2, command.ExecuteNonQuery());
        }

        using var pre046Store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(pre046Store, pre046Store, lookup, options, pre046Store, pre046Store);

            var suggestion = Assert.Single(capsule.SuggestedVerification);
            Assert.Contains("\"Test.slnx\"", suggestion.Command);
        }
        finally
        {
            pre046Store.Close();
        }
    }

    [Fact]
    public async Task Capsule_Verification_FullSuite_NamesTheIndexedSolution()
    {
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["TestTarget.cs"] = TestTargetSource,
            ["LocalCaller.cs"] = """
                                 namespace TestProject;

                                 public class LocalCaller
                                 {
                                     public int Go() => new TestTarget().Run(2);
                                 }
                                 """
        });
        CreateProject("TestProject.Tests",
            new Dictionary<string, string> { ["TestTargetTests.cs"] = ManyTestMethodsSource() },
            projectReferences: ["TestProject"],
            packageReferences: ["xunit@2.9.3"]);
        CreateProject("TestProject.Client",
            new Dictionary<string, string>
            {
                ["Client.cs"] = """
                                namespace TestProject.Client;

                                public class Client
                                {
                                    public int Call() => new TestProject.TestTarget().Run(1);
                                }
                                """
            },
            projectReferences: ["TestProject"]);
        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);
        var anchorId = ResolveSymbolId(snapshotId, "global::TestProject.TestTarget.Run");
        File.WriteAllText(Path.Combine(TestDir, "Aaa.slnx"), "");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 1);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            var fullSuite = Assert.Single(capsule.SuggestedVerification, s => s.TestId == "full-suite");
            Assert.Equal("dotnet test \"Test.slnx\"", fullSuite.Command);
        }
        finally
        {
            store.Close();
        }
    }

    // ── Step 4: witness-path capsule paths and topology (output schema 5) ──

    /// <summary>
    ///     CONTRACT test for the capsule's witness-path topology (Step 4).
    ///     The fixture is 6 layers of 12 static methods; every method calls all
    ///     12 methods of the layer above, and the top layer calls Root. At
    ///     MaxHops = 5 the incoming direction reaches 5 x 12 = 60 synthetic
    ///     symbols, while the old per-path enumerator
    ///     had about 12^5 ≈ 249k terminal paths.
    ///     Assembly must complete, and topology must report the reached-symbol
    ///     and witness-leaf counts, not enumerated path counts.
    /// </summary>
    [Fact]
    public async Task Capsule_LayeredGraph_TopologyCountsWitnessReach()
    {
        CreateProject("Layered", new Dictionary<string, string> { ["Graph.cs"] = LayeredGraphSource() });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var anchorId = ResolveSymbolId(snapshotId, "global::Layered.Graph.Root");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            // Generous budget: the topology counts are taken before the budget
            // trim, so the assertions must not depend on trim behavior.
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 500_000, 5);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.NotNull(capsule.Topology);
            var topology = capsule.Topology!.Current;
            Assert.Equal(60, topology.IncomingSymbolCount);
            // One parent in each of the first four layers has children.
            // The other 11 nodes in each layer and all 12 nodes at depth five are leaves.
            Assert.Equal(56, topology.IncomingWitnessPathCount);
            Assert.True(topology.IncomingWitnessPathCount <= topology.IncomingSymbolCount);
            Assert.Equal(170, topology.TotalHopCount);

            var leafPaths = capsule.IncomingPaths;
            Assert.Equal(12, leafPaths.Count(path => path.TotalSteps == 5));
            Assert.Equal(170, leafPaths.Sum(path => path.TotalSteps));
            Assert.DoesNotContain(leafPaths.SelectMany(path => path.Hops),
                hop => hop.EdgeKind is nameof(EdgeKind.Declares) or nameof(EdgeKind.Contains) or nameof(EdgeKind.TestedBy));
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_ImplementedInterfaceMember_IsAContract()
    {
        CreateProject("Contracts", new Dictionary<string, string>
        {
            ["Contracts.cs"] = """
                               namespace Contracts;
                               public interface I { void Run(); }
                               public interface IPossible { void Run(); }
                               public class Base { public virtual void Run() { } }
                               public class Impl : Base, I { public override void Run() { } }
                               public class Inherited : Impl { }
                               public class Explicit : I { void I.Run() { } }
                               """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        string Id(string name) => ResolveSymbolId(snapshot, $"global::Contracts.{name}");
        var contract = Id("I.Run");
        var possible = Id("IPossible.Run");
        var implementation = Id("Impl.Run");
        store.SaveEdges(snapshot, [new EdgeRecord
        {
            SourceSymbolId = possible, TargetSymbolId = implementation, Kind = nameof(EdgeKind.MayDispatchTo),
            Provenance = Provenance.Possible, ExtractorVersion = VersionConstants.ExtractorVersion
        }]);
        var explicitId = SymbolId.Compose("M:Contracts.Explicit.Contracts#I#Run", SymbolId.Parse(contract).AssemblyIdentity);

        foreach (var id in new[] { implementation, explicitId, Id("Impl"), Id("Explicit") })
        {
            var capsule = Capsule(id);
            var fact = Assert.Single(capsule.Contracts, item => item.SymbolId == contract);
            Assert.Equal(nameof(EdgeKind.MayDispatchTo), fact.EdgeKind);
            Assert.Equal(Provenance.CompilerProved, fact.Provenance);
            Assert.DoesNotContain(capsule.RegisteredImplementations, item => item.SymbolId == contract);
            Assert.DoesNotContain(capsule.Contracts, item => item.SymbolId == possible);
        }

        var overrideCapsule = Capsule(implementation);
        Assert.Contains(overrideCapsule.Contracts, item => item.SymbolId == Id("Base.Run") && item.EdgeKind == "Overrides");
        Assert.Contains(overrideCapsule.RegisteredImplementations, item => item.SymbolId == possible);
        Assert.DoesNotContain(Capsule(Id("Inherited")).Contracts, item => item.SymbolId == contract);

        ContextCapsule Capsule(string id) => ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshot, id, null, null), DefaultOptions(50_000, 2), store, store);
    }

    private static string LayeredGraphSource()
    {
        var source = new StringBuilder();
        source.AppendLine("namespace Layered;");
        source.AppendLine();
        source.AppendLine("public static class Graph");
        source.AppendLine("{");
        source.AppendLine("    public static void Root() { }");
        for (var level = 5; level >= 0; level--)
        {
            for (var node = 0; node < 12; node++)
            {
                if (level == 5)
                {
                    source.AppendLine(CultureInfo.InvariantCulture, $"    public static void L{level}_{node}() => Root();");
                    continue;
                }

                source.AppendLine(CultureInfo.InvariantCulture, $"    public static void L{level}_{node}()");
                source.AppendLine("    {");
                for (var callee = 0; callee < 12; callee++)
                    source.AppendLine(CultureInfo.InvariantCulture, $"        L{level + 1}_{callee}();");
                source.AppendLine("    }");
            }
        }

        source.AppendLine("}");
        return source.ToString();
    }

    [Fact]
    public void PathBound_KeepsTheFirstWholePaths()
    {
        static ImpactPath MakePath(string leafId) => new([
            new ImpactHop("U", "T", "Calls", Provenance.CompilerProved),
            new ImpactHop("V", "U", "Calls", Provenance.CompilerProved),
            new ImpactHop("W", "V", "Calls", Provenance.CompilerProved),
            new ImpactHop(leafId, "W", "Calls", Provenance.CompilerProved)
        ]);

        static ContextCapsule MakeCapsule(IEnumerable<ImpactPath> paths)
        {
            var capsule = new ContextCapsule(new CapsuleAnchor("T", "T", nameof(IndexedSymbolKind.Method), string.Empty));
            capsule.IncomingPaths.AddRange(paths);
            return capsule;
        }

        var leaves = Enumerable.Range(1, 5).Select(i => $"L{i}").ToList();
        var threePathCopy = MakeCapsule(leaves.Take(3).Select(MakePath));
        var budget = CapsuleBudgetEnforcer.Measure(threePathCopy);

        var capsule = MakeCapsule(leaves.Select(MakePath));
        CapsuleBudgetEnforcer.Enforce(capsule, budget, ContextAssembler.TierNames);

        Assert.Equal(3, capsule.IncomingPaths.Count);
        for (var i = 0; i < 3; i++)
        {
            var expected = MakePath(leaves[i]);
            var actual = capsule.IncomingPaths[i];
            Assert.Equal(4, actual.TotalSteps);
            Assert.Equal(4, actual.Hops.Count);
            Assert.Equal(
                expected.Hops.Select(h => (h.SourceSymbolId, h.TargetSymbolId, h.EdgeKind)),
                actual.Hops.Select(h => (h.SourceSymbolId, h.TargetSymbolId, h.EdgeKind)));
        }

        Assert.Equal(3, capsule.IncomingPaths.Select(p => p.Hops[^1].SourceSymbolId).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(capsule.OmittedTiers, e => e is { Category: "incoming_paths", Reason: "summarized" });
    }

    [Fact]
    public void TrimOrder_FollowsD64()
    {
        static CapsuleItem MakeItem(string id) => new(id, nameof(IndexedSymbolKind.Method), id, Provenance.CompilerProved, "Calls", "public void M() {}");

        var capsule = new ContextCapsule(new CapsuleAnchor("T:A", "A", nameof(IndexedSymbolKind.Type), "public class A {}"))
        {
            Completeness = new SnapshotCompleteness().WithBindingIncompleteness(
                [new BindingIncompletenessRecord("Proj", "a.cs", "compiler_error", 1, "1.12.0")],
                includeDetail: true),
            Topology = new CapsuleTopology(new CapsuleTopologyReference("see incoming", "see outgoing", 1, 1, 1, 1, 2))
        };
        foreach (var tierName in ContextAssembler.TierNames)
            ContextAssembler.AddTierToCapsule(capsule, tierName, [MakeItem($"M:{tierName}")]);

        capsule.InclusionReasons["contracts"] = "r1";
        capsule.InclusionReasons["direct_callers"] = "r2";
        capsule.AffectedPublicSurfaces.Add(MakeItem("M:Surface"));
        capsule.LikelyChangeSites.Add(new LikelyChangeSite("a.cs", 0, "anchor", "T:A"));
        capsule.Constraints.Add(new CapsuleConstraint("c", "annotation"));
        capsule.SemanticCauses.Add(new SemanticCauseJson("s1", "s2", "signature_changed", "T:A", null));
        capsule.IncomingPaths.Add(new ImpactPath([new ImpactHop("B", "T:A", "Calls", Provenance.CompilerProved)]));
        capsule.OutgoingPaths.Add(new ImpactPath([new ImpactHop("T:A", "C", "Calls", Provenance.CompilerProved)]));
        capsule.SuggestedVerification.Add(new VerificationSuggestion("T:Test", "Test", "run", "dotnet test"));
        capsule.Uncertainties.Add(new UncertaintyEntry(["T:A"], "location_gap", "keep me"));

        CapsuleBudgetEnforcer.Enforce(capsule, 0, ContextAssembler.TierNames);

        Assert.Equal(
            [
                "surrounding_source",
                "inclusion_reasons",
                "affected_public_surfaces",
                "likely_change_sites",
                "completeness",
                "constraints",
                "topology",
                "semantic_causes",
                "outgoing_paths",
                "incoming_paths",
                "suggested_verification",
                "second_degree_context",
                "relevant_tests",
                "registered_implementations",
                "direct_callers",
                "direct_callees",
                "contracts",
                "anchor"
            ],
            capsule.OmittedTiers.Select(e => e.Category));
        Assert.Single(capsule.Uncertainties);
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
        // CLI_REFERENCE.md "estimated_tokens": characters / 3. The source is 36 characters: 36 / 3 = 12.
        Assert.Equal(12, ContextAssembler.EstimateTokens(withSource));

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
    ///     CapsuleBudgetEnforcer's retained floor (the omitted_tiers_* recovery
    ///     instruction, ~150-195 tokens at CharsPerToken 3 for a capsule with budget_exhausted tiers)
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
        // it is charged PathOnlyFramingChars / CharsPerToken = 53 tokens.
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
        // Boundary budget: anchor + source tier + 20, less than the 53-token
        // path-only framing charge. PathOnlyFramingChars is the deciding factor:
        // the path tier fits at anchor + source tier + 0 (old estimate) but not
        // at + 53.
        var budget = anchorCost + sourceTierCost + 20;

        ContextBudgeter.Apply(
            capsule,
            [
                new FixedTierBuilder("direct_callers", sourceItem),
                new FixedTierBuilder("contracts", pathItem)
            ],
            budget,
            anchorCost,
            new BindingObservation(false, false, false, false));

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

    [Fact]
    public async Task Capsule_PipelineBehavior_SurfacesUnmodeledMediatRUncertainty()
    {
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
        CreateProject("Other", new Dictionary<string, string> { ["Other.cs"] = "namespace Other; public class Unrelated { }" });

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);

        var ping = ResolveSymbolId(snapshotId, "global::App.Ping");
        var behavior = SymbolId.Compose("T:App.LoggingBehavior`2", SymbolId.Parse(ping).AssemblyIdentity);
        var handler = ResolveSymbolId(snapshotId, "global::App.PingHandler");
        var handlerCapsule = Capsule(handler);
        Assert.Contains(handlerCapsule.DirectCallers, item => item.SymbolId == ping && item.EdgeKind == "Handles");

        foreach (var id in new[]
                 {
                     handler,
                     ResolveSymbolId(snapshotId, "global::App.PingHandler.Handle"),
                     ping, behavior
                 })
            Assert.Equal([behavior], Assert.Single(Warnings(id)).SymbolIds);

        Assert.Empty(Warnings(ResolveSymbolId(snapshotId, "global::Other.Unrelated")));

        ContextCapsule Capsule(string id) => ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshotId, id, null, null), DefaultOptions(50_000, 1), store, store);

        IEnumerable<UncertaintyEntry> Warnings(string id) =>
            Capsule(id).Uncertainties.Where(entry => entry.RelationshipKind == "unmodeled_mediatr_pattern");
    }

    // ── B23 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capsule_Annotations_RequireAnEmittedSymbolAndAConstraintKind()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["App.cs"] = """
                         namespace App;
                         public class Outer
                         {
                             public class Inner
                             {
                                 public void Anchor() => Helper.Entry();
                             }
                         }
                         public static class Helper
                         {
                             public static void Entry() => Middle();
                             public static void Middle() => End();
                             public static void End() { }
                         }
                         """
        });
        CreateProject("Other", new Dictionary<string, string> { ["Other.cs"] = "namespace Other; public class Unrelated { }" });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        string Id(string name) => ResolveSymbolId(snapshot, $"global::{name}");
        var anchor = Id("App.Outer.Inner.Anchor");
        var middle = Id("App.Helper.Middle");
        var unrelated = Id("Other.Unrelated");
        (string Id, string Kind)[] expected =
        [
            (anchor, "constraint"), (Id("App.Outer.Inner"), "invariant"),
            (Id("App.Outer"), "ef_query_filter_constraint"), (middle, "app_invariant")
        ];
        store.SaveAnnotations(snapshot, expected.Select(pair => new AnnotationRecord(pair.Id, pair.Kind, pair.Kind))
            .Concat([
                new AnnotationRecord(unrelated, "constraint", "unrelated constraint"),
                new AnnotationRecord(anchor, "constraint_note", "note"),
                new AnnotationRecord(anchor, "nonconstraint", "other"),
                new AnnotationRecord(anchor, "serialization_contract", "serialization"),
                new AnnotationRecord(middle, "unmodeled_mediatr_pattern", "IStreamRequestHandler"),
                new AnnotationRecord(unrelated, "unmodeled_mediatr_pattern", "IStreamRequestHandler")
            ]));

        var capsule = ContextAssembler.ResolveAndAssemble(store, store, new ContextLookup(snapshot, anchor, null, null),
            DefaultOptions(50_000, 3), store, store);
        Assert.DoesNotContain(capsule.DirectCallees, item => item.SymbolId == middle);
        Assert.Contains(capsule.OutgoingPaths.SelectMany(path => path.Hops), hop => hop.TargetSymbolId == middle);
        Assert.Equal(expected, capsule.Constraints.Select(entry => (entry.SymbolId!, entry.AnnotationKind!)));
        var warning = Assert.Single(capsule.Uncertainties, entry => entry.RelationshipKind == "unmodeled_mediatr_pattern");
        Assert.Equal([middle], warning.SymbolIds);

        var annotations = new CapsuleAnnotationScope([Id("App.Outer.Inner"), Id("App.Outer")], false,
            store.GetAnnotations(snapshot));
        foreach (var keepItem in new[] { false, true })
        {
            var bounded = new ContextCapsule(capsule.Anchor);
            var entry = Id("App.Helper.Entry");
            var end = Id("App.Helper.End");
            var inner = Id("App.Outer.Inner");
            bounded.OutgoingPaths.Add(new ImpactPath([
                new ImpactHop(anchor, entry, "Calls", Provenance.CompilerProved)
            ]));
            bounded.OutgoingPaths.Add(new ImpactPath([
                new ImpactHop(anchor, end, "Calls", Provenance.CompilerProved)
            ]));
            bounded.OutgoingPaths.Add(new ImpactPath([
                new ImpactHop(anchor, inner, "References", Provenance.CompilerProved)
            ]));
            bounded.OutgoingPaths.Add(new ImpactPath([
                new ImpactHop(anchor, entry, "Calls", Provenance.CompilerProved),
                new ImpactHop(entry, end, "Calls", Provenance.CompilerProved),
                new ImpactHop(end, inner, "References", Provenance.CompilerProved),
                new ImpactHop(inner, middle, "Calls", Provenance.CompilerProved, new string('x', 4_000))
            ]));
            if (keepItem)
                bounded.DirectCallees.Add(new CapsuleItem(middle, "Method", "global::App.Helper.Middle",
                    Provenance.CompilerProved, "Calls"));
            annotations.AddConstraints(bounded);
            Assert.Contains(bounded.Constraints, constraint => constraint.SymbolId == middle);

            // The fourth path alone costs over 1,300 tokens; the first three paths fit whole.
            CapsuleBudgetEnforcer.Enforce(bounded, 1_200, ContextAssembler.TierNames, annotations);

            Assert.Equal(3, bounded.OutgoingPaths.Count);
            Assert.DoesNotContain(bounded.OutgoingPaths.SelectMany(path => path.Hops),
                hop => hop.SourceSymbolId == middle || hop.TargetSymbolId == middle);
            Assert.Equal(keepItem, bounded.DirectCallees.Any(item => item.SymbolId == middle));
            Assert.Equal(expected.Take(keepItem ? 4 : 3),
                bounded.Constraints.Select(constraint => (constraint.SymbolId!, constraint.AnnotationKind!)));

            new UncertaintyDetector(store, store, snapshot, SymbolId.Parse(anchor), false, null)
                .PopulateUncertainties(bounded, annotations.MediatRPatterns(bounded));
            var streamIds = bounded.Uncertainties.Where(item => item.RelationshipKind == "unmodeled_mediatr_pattern")
                .SelectMany(item => item.SymbolIds);
            Assert.Equal(keepItem ? [middle] : Array.Empty<string>(), streamIds);
        }
    }

    [Fact]
    public async Task Capsule_CarriesAnchorSemanticCausesOnce_AtTheTopLevel()
    {
        CreateProject("CauseProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace CauseProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller() { Bar(); }
                    }
                }
                """
        });
        await RunFullIndexAsync(DbPath);

        // A signature change gives the anchor a semantic cause against the previous snapshot.
        WriteFile("CauseProj", "Models.cs", """
            namespace CauseProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller() { Bar(1); }
                }
            }
            """);
        var snapshotId = await RunIncrementalIndexAsync();

        using var store = OpenStore(DbPath);
        try
        {
            var barId = store.GetSymbolIdsInSnapshot(snapshotId)
                .First(id => store.GetSymbolInfo(id, snapshotId)?.FullyQualifiedName?.Contains("CauseProj.Foo.Bar") == true);
            var lookup = new ContextLookup(snapshotId, barId, null, null);
            var options = DefaultOptions(5000, 1);

            var withCauses = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store, store);
            using var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(withCauses));
            var root = document.RootElement;

            var pathCount = root.GetProperty("incoming_paths").GetArrayLength() + root.GetProperty("outgoing_paths").GetArrayLength();
            Assert.True(pathCount >= 1, "The fixture needs at least one path, or the check that no path carries causes proves nothing.");

            var causes = root.GetProperty("semantic_causes");
            Assert.True(causes.GetArrayLength() >= 1, "Expected the anchor's semantic causes at the top level.");
            Assert.All(causes.EnumerateArray(), cause => Assert.Equal(barId, cause.GetProperty("symbol_id").GetString()));

            // Each path carries no causes of its own: the list is written once for the capsule.
            foreach (var name in new[] { "incoming_paths", "outgoing_paths" })
                if (root.TryGetProperty(name, out var paths))
                    Assert.All(paths.EnumerateArray(), path => Assert.False(path.TryGetProperty("semantic_causes", out _)));

            var withoutCauses = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);
            using var emptyDocument = JsonDocument.Parse(ContextCapsuleJson.Serialize(withoutCauses));
            Assert.Empty(emptyDocument.RootElement.GetProperty("semantic_causes").EnumerateArray());
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_ManySemanticCauses_AreBoundedToTen_UnderBudget()
    {
        CreateProject("CauseProj", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace CauseProj {
                    public class Foo {
                        public void Bar() {}
                        public void Caller1() { Bar(); }
                        public void Caller2() { Bar(); }
                        public void Caller3() { Bar(); }
                        public void Caller4() { Bar(); }
                    }
                }
                """
        });
        var fromSnapshotId = await RunFullIndexAsync(DbPath);

        WriteFile("CauseProj", "Models.cs", """
            namespace CauseProj {
                public class Foo {
                    public void Bar(int x) {}
                    public void Caller1() { Bar(1); }
                    public void Caller2() { Bar(1); }
                    public void Caller3() { Bar(1); }
                    public void Caller4() { Bar(1); }
                }
            }
            """);
        var snapshotId = await RunIncrementalIndexAsync();

        string barId;
        using (var lookupStore = OpenStore(DbPath))
            barId = lookupStore.GetSymbolIdsInSnapshot(snapshotId)
                .First(id => lookupStore.GetSymbolInfo(id, snapshotId)?.FullyQualifiedName?.Contains("CauseProj.Foo.Bar") == true);

        // The signature change gives one cause. Eleven more for the same anchor make twelve,
        // so the anchor has more than the ten causes the capsule keeps under budget.
        using (var connection = OpenDbConnection())
        {
            var extraCauses = Enumerable.Range(1, 11).Select(i => new SemanticChange
            {
                ChangeId = $"b60-extra-cause-{i:D2}",
                FromSnapshotId = fromSnapshotId,
                ToSnapshotId = snapshotId,
                ChangeType = "signature_changed",
                SymbolId = barId,
                DetailJson = $"{{\"n\":{i}}}",
                CreatedAtUtc = DateTime.UtcNow
            }).ToList();
            new SemanticDiffStore(connection).SaveSemanticChanges(fromSnapshotId, snapshotId, extraCauses);
        }

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, barId, null, null);

            // Unbudgeted, the capsule keeps every path and every cause; its measure is the budget basis.
            var full = ContextAssembler.ResolveAndAssemble(store, store, lookup, DefaultOptions(1_000_000, 1), store, store, store);
            Assert.True(full.IncomingPaths.Count > 3, "The fixture needs more than three incoming paths.");

            var bounded = ContextAssembler.ResolveAndAssemble(store, store, lookup, DefaultOptions(full.EstimatedTokens - 1, 1), store, store, store);
            using var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(bounded));
            var root = document.RootElement;

            Assert.Equal(10, root.GetProperty("semantic_causes").GetArrayLength());
            Assert.Contains(root.GetProperty("omitted_tiers").EnumerateArray(),
                entry => entry.GetProperty("category").GetString() == "semantic_causes"
                         && entry.GetProperty("reason").GetString() == "summarized");

            var incoming = root.GetProperty("incoming_paths");
            Assert.InRange(incoming.GetArrayLength(), 1, 3);
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_PathHop_ShowsTheSitesOfItsEdge()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["C.cs"] = """
                namespace N;
                public class C
                {
                    public void Target() { }
                    public void Twice()
                    {
                        Target();
                        Target();
                    }
                }
                """
        });

        var snapshotId = await RunFullIndexAsync(DbPath);
        var targetId = ResolveSymbolId(snapshotId, "global::N.C.Target");
        var twiceId = ResolveSymbolId(snapshotId, "global::N.C.Twice");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, targetId, null, null);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, DefaultOptions(100_000, 2), store, store, store);

            using var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(capsule));
            var callHop = document.RootElement.GetProperty("incoming_paths").EnumerateArray()
                .SelectMany(p => p.GetProperty("hops").EnumerateArray())
                .Single(h => h.GetProperty("source_symbol_id").GetString() == twiceId
                             && h.GetProperty("target_symbol_id").GetString() == targetId
                             && h.GetProperty("edge_kind").GetString() == "Calls");

            Assert.Equal(7, callHop.GetProperty("source_line").GetInt32());
            Assert.Equal(2, callHop.GetProperty("site_count").GetInt32());

            var sites = callHop.GetProperty("sites").EnumerateArray()
                .Select(s => (
                    Line: s.GetProperty("line").GetInt32(),
                    Column: s.GetProperty("column").GetInt32(),
                    EndLine: s.GetProperty("end_line").GetInt32(),
                    EndColumn: s.GetProperty("end_column").GetInt32()))
                .ToList();

            Assert.Equal(
                [
                    (7, 8, 7, 16),
                    (8, 8, 8, 16)
                ],
                sites);
        }
        finally
        {
            store.Close();
        }
    }

    [Fact]
    public async Task Capsule_ExtractorIdentity_IsTheSnapshots()
    {
        var (snapshotId, _, usedId, _, _, _) = await IndexFixtureAsync();

        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET extractor_version = '1.10.0' WHERE snapshot_id = $snapshot;";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, usedId, null, null);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, DefaultOptions(), store, store);

            Assert.Equal("1.10.0", capsule.Anchor.ExtractorIdentity);
            Assert.NotNull(capsule.Completeness);
            Assert.Equal("1.10.0", capsule.Completeness!.ExtractorVersion);
        }
        finally
        {
            store.Close();
        }
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
        public TierBindingScope BindingScope => TierBindingScope.CallerProjects;

        public List<CapsuleItem> Build()
        {
            return [.. items];
        }
    }
}
