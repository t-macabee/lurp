using Lurp.Handlers;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class ContextCapsuleSurfaceTests : IntegrationTestBase
{
    [Fact]
    public async Task Capsule_PublicSurfaces_UseEffectiveAccessAndExternalFriends()
    {
        CreateProject("FriendProject", new Dictionary<string, string> { ["Friend.cs"] = "class Friend { }" },
            msbuildProperties: new Dictionary<string, string> { ["AssemblyName"] = "FriendAssembly" });
        CreateCore("NoFriend", null);
        CreateCore("Outside", "ExternalAssembly");
        CreateCore("Inside", "friendassembly");
        CreateCore("Wildcard", "*");
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);

        Check("NoFriend.Hidden.Run", false);
        Check("Outside.Hidden.Run", true);
        Check("Inside.Hidden.Run", false);
        Check("Wildcard.Hidden.InternalRun", true);
        Check("NoFriend.Hidden.Value", false);
        Check("Outside.Hidden.Value", true);
        Check("Outside.Hidden.Nested.Value", false);
        Check("Outside.Hidden.Run", false, omitProjectFacts: true);

        var legacy = ResolveSymbolId(snapshot, "global::NoFriend.Hidden.Run");
        using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE snapshot_symbols SET metadata_json = json_remove(metadata_json, '$.effective_accessibility')
                WHERE snapshot_id = $snapshot AND symbol_id = $id;
                """;
            command.Parameters.AddWithValue("$snapshot", snapshot);
            command.Parameters.AddWithValue("$id", legacy);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        Check("NoFriend.Hidden.Run", true);

        void CreateCore(string project, string? grant)
        {
            var attribute = grant == null ? "" : $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{grant}\")]";
            CreateProject(project, new Dictionary<string, string>
            {
                ["Hidden.cs"] = $$"""
                                {{attribute}}
                                namespace {{project}};
                                internal class Hidden
                                {
                                    public void Run() { }
                                    internal void InternalRun() { }
                                    public int Value { get; }
                                    private class Nested
                                    {
                                        public int Value { get; }
                                    }
                                }
                                """
            }, msbuildProperties: new Dictionary<string, string> { ["AssemblyName"] = $"{project}Assembly" });
        }

        void Check(string name, bool present, bool omitProjectFacts = false)
        {
            var id = ResolveSymbolId(snapshot, $"global::{name}");
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, new ContextLookup(snapshot, id, null, null),
                new ContextAssemblyOptions(ContextIntent.Inspect, 50_000, ContextRequest.DefaultMaxHops), store, omitProjectFacts ? null : store);
            Assert.Equal(present ? [id] : Array.Empty<string>(), capsule.AffectedPublicSurfaces.Select(item => item.SymbolId));
            if (!present)
                Assert.Contains(capsule.OmittedTiers, entry => entry.Category == "affected_public_surfaces" &&
                    entry.Reason == (omitProjectFacts ? "unresolved" : "empty"));
            if (omitProjectFacts)
                Assert.Contains("friend grants", capsule.InclusionReasons["omitted_tiers_unresolved"]);
        }
    }
}

public sealed class ContextCapsuleAcceptanceTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"lurp-capsule-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, true);
    }

    [Fact]
    public async Task SelfHost_EdgeLocationResolver_CapsuleSatisfiesPhase15Contract()
    {
        Directory.CreateDirectory(_outputDir);
        var repositoryRoot = LocateRepositoryRoot();
        var solutionPath = Path.Combine(repositoryRoot, "Lurp.slnx");
        var dbPath = Path.Combine(_outputDir, "index.db");
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var anchor = HandlerBootstrap.ResolveSymbolInfo(store, "Lurp.Shared.EdgeLocationResolver", snapshotId);

        var capsule = ContextAssembler.ResolveAndAssemble(
            store,
            store,
            new ContextLookup(snapshotId, anchor.SymbolId.Value, null, null),
            new ContextAssemblyOptions(
                ContextIntent.Modify,
                // Generous enough to hold the full self-host capsule untrimmed
                // (its type-anchor tiers carry member source bodies). This test
                // proves the Phase-15 structural contract is present, not that
                // the budget truncates; budget truthfulness is exercised by
                // CapsuleCharacterizationTests and the --budget CLI criterion.
                500_000,
                3,
                Scope: "src/Shared/EdgeLocationResolver.cs",
                AffectedProjects: ["Lurp"]),
            store,
            store);

        // Anchor identity: the capsule is anchored on the resolved self-host symbol
        // in the snapshot that was just indexed.
        Assert.Equal(snapshotId, capsule.Anchor.SnapshotId);
        Assert.NotEmpty(capsule.Anchor.Locations);

        // EdgeLocationResolver implements no interface, is not DI-registered, and
        // has no direct test coverage (EdgeLocationResolverTests was removed with
        // f1254fc), so these three tiers are empty. They must still prove that
        // emptiness through the reason-coded OmittedTiers channel, never as a bare
        // non-null-but-empty collection.
        AssertSectionPresentOrOmitted(capsule, "contracts", capsule.Contracts);
        AssertSectionPresentOrOmitted(capsule, "registered_implementations", capsule.RegisteredImplementations);
        AssertSectionPresentOrOmitted(capsule, "relevant_tests", capsule.RelevantTests);

        // Populated sections: the anchor has real callers/callees and a live
        // neighborhood, so uncertainties, paths, change sites, and inclusion
        // reasons are all present.
        Assert.NotEmpty(capsule.Uncertainties);
        Assert.True(capsule.IncomingPaths.Count + capsule.OutgoingPaths.Count > 0);

        // Stable value assertions on the self-host fixture. These are core
        // production callers/callees of EdgeLocationResolver that are part of the
        // extraction pipeline and unlikely to be removed when src/ changes;
        // locations are pinned to the source file that declares the anchor.
        Assert.Contains(capsule.DirectCallees,
            item => item.FullyQualifiedName == "global::Lurp.Shared.PathNormalizer.ToForwardSlash");
        // S01-F32: one EdgeLocationResolver for each project, built in ExtractAll.
        Assert.Contains(capsule.DirectCallers,
            item => item.FullyQualifiedName == "global::Lurp.Workspace.CompilationFactExtractor.ExtractAll");
        Assert.Contains(capsule.Anchor.Locations,
            loc => loc.DocumentPath == "src/Shared/EdgeLocationResolver.cs");

        // Verification section. The original Phase-15 test asserted a concrete
        // "dotnet test" suggestion, which was fed by EdgeLocationResolver's own
        // TestedBy coverage (EdgeLocationResolverTests, removed in f1254fc) or by a
        // >=3-project blast radius. Neither holds for this anchor now, so the
        // section is empty. It must still exist, and any suggestion it does carry
        // must be a runnable command.
        Assert.NotNull(capsule.SuggestedVerification);
        Assert.All(capsule.SuggestedVerification,
            step => Assert.False(string.IsNullOrWhiteSpace(step.Command)));

        Assert.NotEmpty(capsule.LikelyChangeSites);
        Assert.NotEmpty(capsule.InclusionReasons);

        // Every item that carries source must state why it was included.
        Assert.All(AllItems(capsule).Where(item => item.Source != null), item => Assert.NotNull(item.InclusionReason));

        // Public surfaces: EdgeLocationResolver is public, so it (and its public
        // callers) surface here; if empty it must be reason-coded.
        AssertSectionPresentOrOmitted(capsule, "affected_public_surfaces", capsule.AffectedPublicSurfaces);

        // Constraints section. The original test asserted a "caller_supplied"
        // constraint, which came from the CallerConstraints assembly option removed
        // in c732a96. The section is now populated only from constraint/invariant
        // annotations; EdgeLocationResolver has none, so it is present but empty.
        Assert.NotNull(capsule.Constraints);
        Assert.All(capsule.Constraints, constraint => Assert.False(string.IsNullOrWhiteSpace(constraint.Value)));

        // Topology: the capsule carries a single reference summary; a symbol with
        // callers and callees must have a non-zero hop count.
        Assert.NotNull(capsule.Topology);
        Assert.True(capsule.Topology!.Current.TotalHopCount > 0);

        // Completeness must be hydrated from the persisted snapshot manifest.
        Assert.NotNull(capsule.Completeness);
    }

    // A section that is genuinely empty for this anchor must prove it through
    // the reason-coded OmittedTiers channel instead of a vacuous Assert.NotNull
    // on a collection that is non-null by construction. The reason is "empty"
    // when the region's bindings were observable. "unresolved" would only apply
    // if the anchor sat in an unobservable binding region — but the sole
    // unobservable record for EdgeLocationResolver (nameof(gitRoot) recorded as
    // unsupported_syntax) is now skipped by CallsEdgeExtractor, so these tiers
    // must prove their emptiness as "empty", not "unresolved".
    private static void AssertSectionPresentOrOmitted(
        ContextCapsule capsule, string category, List<CapsuleItem> items)
    {
        if (items.Count > 0)
            return;
        Assert.Contains(capsule.OmittedTiers,
            entry => entry.Category == category && entry.Reason == "empty");
    }

    private static IEnumerable<CapsuleItem> AllItems(ContextCapsule capsule)
    {
        return capsule.Contracts
            .Concat(capsule.DirectCallees)
            .Concat(capsule.DirectCallers)
            .Concat(capsule.RegisteredImplementations)
            .Concat(capsule.RelevantTests)
            .Concat(capsule.SecondDegreeContext)
            .Concat(capsule.SurroundingSource);
    }

    private static string LocateRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lurp.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
