using Lurp.Storage;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
///     Phase 4 characterization scaffold for the dead-candidate detector
///     (<c>lurp-phases-report.md</c> Phase 2 design, Phase 3 implementation at
///     commit <c>4cb8ae4</c>, Phase 4 verification). Pins the suppression-ladder
///     contract of <see cref="DeadCandidateStore.GetDeadCandidatesPage" />: one
///     <c>reason</c>/<c>status</c> per candidate, batched incoming-edge queries,
///     and the <c>include_public</c>/<c>include_generated</c> toggles.
///     Uses <see cref="IntegrationTestBase" /> (real MSBuild indexing) for natural
///     fixtures, plus direct <c>store.SaveEdges</c> injection to isolate individual
///     weak-provenance ladder branches that no single natural C# construct can
///     produce in isolation from the others (see
///     <see cref="PossibleDispatch_InheritedOnly_Uncertain" />).
/// </summary>
public sealed class DeadCandidateCharacterizationTests : IntegrationTestBase
{
    private static EdgeRecord MakeEdge(string source, string target, string kind, string provenance)
    {
        return new EdgeRecord
        {
            SourceSymbolId = source,
            TargetSymbolId = target,
            Kind = kind,
            Provenance = provenance,
            ExtractorVersion = "0.0.0-injected",
            SourceDocumentPath = "src/TestProject/Injected.cs",
            SourceStartLine = 1,
            SourceStartColumn = 1,
            SourceEndLine = 1,
            SourceEndColumn = 1
        };
    }

    [SkippableFact]
    public async Task ProvedDead_InternalHelper_NoLiveIncoming()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Util.cs"] = """
                          namespace TestProject;

                          internal static class Util
                          {
                              internal static void Helper() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::TestProject.Util.Helper");

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == helperId);
            Assert.Equal(DeadCandidateStatus.ProvedDead, entry.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, entry.Reason);
            Assert.Empty(entry.Uncertainties);
            Assert.True(page.DeadCount >= 1);
            Assert.True(page.CandidateCount >= 1);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     Characterization for the entry-point suppression branch. Before this branch existed,
    ///     the compiler-synthesized top-level-statements entry point method landed in the
    ///     terminal no-incoming-edges branch and read as <c>proved_dead</c> — reproduced live
    ///     against a real solution during the eNoteV2/FIT-RS2-2026 capability battery
    ///     (2026-08-21) and confirmed here by dedicated repro: nothing in-repo ever calls the
    ///     entry point (only the runtime launcher does), so "no incoming live edges" is
    ///     definitional for this symbol, not evidence of dead code. Program.cs holds only the
    ///     top-level statements — Greeter lives in a separate file — so the document filter
    ///     below isolates exactly the synthesized entry-point method as the sole Method-kind
    ///     candidate declared there.
    /// </summary>
    [SkippableFact]
    public async Task ProcessEntryPoint_TopLevelStatements_IsUncertainNotProvedDead()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("EntryPointProbe",
            new Dictionary<string, string>
            {
                ["Program.cs"] = """
                                  var svc = new EntryPointProbe.Greeter();
                                  svc.Greet();
                                  """,
                ["Greeter.cs"] = """
                                 namespace EntryPointProbe;

                                 public class Greeter
                                 {
                                     public void Greet() => System.Console.WriteLine("hi");
                                 }
                                 """
            },
            msbuildProperties: new Dictionary<string, string> { ["OutputType"] = "Exe" });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, "src/EntryPointProbe/Program.cs", "Method", false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates);
            Assert.Equal(DeadCandidateStatus.UncertainDead, entry.Status);
            Assert.Equal(DeadCandidateReason.EntryPointConvention, entry.Reason);
            Assert.NotEqual(DeadCandidateStatus.ProvedDead, entry.Status);
            Assert.Single(entry.Uncertainties);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task ProvedLive_ViaCallsReadsWritesConstructs()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Counter.cs"] = """
                             namespace TestProject;

                             public class Counter
                             {
                                 private int _v;
                                 public int Get() => _v;
                                 public void Set(int x) => _v = x;
                             }

                             public class Consumer
                             {
                                 public int Use() => new Counter().Get();
                                 public void Make() { var c = new Counter(); c.Set(1); }
                             }
                             """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var counterTypeId = ResolveSymbolId(snapshotId, "global::TestProject.Counter");
        var getId = ResolveSymbolId(snapshotId, "global::TestProject.Counter.Get");
        var fieldId = ResolveSymbolId(snapshotId, "global::TestProject.Counter._v");

        // Sanity: the fixture actually produced the LIVE edges this test relies on.
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Reads), Provenance.CompilerProved));
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Writes), Provenance.CompilerProved));
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Constructs), Provenance.CompilerProved));
        Assert.NotEmpty(QueryEdges(snapshotId, nameof(EdgeKind.Calls), Provenance.CompilerProved));

        using var store = OpenStore(DbPath);
        try
        {
            // include_public=true so a false "alive" verdict can't hide behind the
            // Q1 exclusion — if any of these leaked through as dead they would still
            // show up here as public_surface.
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, false, false, 200, null);

            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == counterTypeId);
            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == getId);
            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == fieldId);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task PossibleDispatch_InheritedOnly_Uncertain()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // NOTE ON FIXTURE CHOICE: InterfaceDispatchExtractor
        // (src/Workspace/InterfaceDispatchExtractor.cs) can only emit a "possible"
        // MayDispatchTo edge for a member that implicitly implements an interface —
        // which C# requires to be `public` — and GetDeadCandidatesPage evaluates the
        // public_surface branch (Q1) BEFORE the weak-provenance branch that yields
        // possible_dispatch. So a naturally-occurring "possible"-only MayDispatchTo
        // witness on real compiled code always surfaces as public_surface, never
        // possible_dispatch — verified below in
        // PossibleDispatch_InheritedOnly_SurfacesAsPublicSurface. This test isolates
        // the possible_dispatch ladder branch itself by injecting the exact edge
        // shape InterfaceDispatchExtractor emits for an inherited (non-direct)
        // interface implementation onto a real internal declared symbol, so the
        // branch is exercised the same way the store would exercise it for any
        // other weak-provenance MayDispatchTo witness on a non-public candidate.
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Worker.cs"] = """
                            namespace TestProject;

                            internal class Worker
                            {
                                internal void DoWork() { }
                            }
                            """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var doWorkId = ResolveSymbolId(snapshotId, "global::TestProject.Worker.DoWork");

        using var store = OpenStore(DbPath);
        try
        {
            store.SaveEdges(snapshotId,
            [
                MakeEdge("I:TestProject.IWorker.DoWork()|TestProject", doWorkId,
                    nameof(EdgeKind.MayDispatchTo), Provenance.Possible)
            ]);

            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == doWorkId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, entry.Status);
            Assert.Equal(DeadCandidateReason.PossibleDispatch, entry.Reason);
            var uncertainty = Assert.Single(entry.Uncertainties);
            Assert.Contains("Manually verify that the runtime dispatch reaches the correct implementation",
                uncertainty.Description);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     Companion to <see cref="PossibleDispatch_InheritedOnly_Uncertain" />:
    ///     proves, on real compiled code with no injected edges, that an inherited
    ///     (non-direct) implicit interface implementation produces exactly one
    ///     MayDispatchTo edge with "possible" provenance, and that
    ///     GetDeadCandidatesPage classifies its target as public_surface (Q1) rather
    ///     than possible_dispatch — public_surface wins because it is evaluated
    ///     first in the ladder and the target must be public to implicitly satisfy
    ///     an interface.
    /// </summary>
    [SkippableFact]
    public async Task PossibleDispatch_InheritedOnly_SurfacesAsPublicSurface()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Worker.cs"] = """
                            namespace TestProject;

                            public interface IWorker { void DoWork(); }

                            public class WorkerBase
                            {
                                public void DoWork() { }
                            }

                            public class Worker : WorkerBase, IWorker
                            {
                            }
                            """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var doWorkId = ResolveSymbolId(snapshotId, "global::TestProject.WorkerBase.DoWork");

        var dispatch = Assert.Single(QueryEdges(snapshotId, nameof(EdgeKind.MayDispatchTo), Provenance.Possible));
        Assert.Equal(doWorkId, dispatch.TargetSymbolId);
        Assert.DoesNotContain(QueryEdges(snapshotId, nameof(EdgeKind.MayDispatchTo), Provenance.CompilerProved),
            e => e.TargetSymbolId == doWorkId);

        using var store = OpenStore(DbPath);
        try
        {
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);
            Assert.DoesNotContain(defaultPage.Candidates, c => c.SymbolId == doWorkId);

            var publicPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, false, false, 200, null);
            var entry = Assert.Single(publicPage.Candidates, c => c.SymbolId == doWorkId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, entry.Status);
            Assert.Equal(DeadCandidateReason.PublicSurface, entry.Reason);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task Convention_NameCandidate_RuntimeUnknown_Uncertain()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Targets.cs"] = """
                             namespace TestProject;

                             internal class Targets
                             {
                                 internal void ConventionTarget() { }
                                 internal void NameCandidateTarget() { }
                                 internal void RuntimeUnknownTarget() { }
                             }
                             """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        var conventionId = ResolveSymbolId(snapshotId, "global::TestProject.Targets.ConventionTarget");
        var nameCandidateId = ResolveSymbolId(snapshotId, "global::TestProject.Targets.NameCandidateTarget");
        var runtimeUnknownId = ResolveSymbolId(snapshotId, "global::TestProject.Targets.RuntimeUnknownTarget");

        using var store = OpenStore(DbPath);
        try
        {
            store.SaveEdges(snapshotId,
            [
                MakeEdge("route://synthetic/convention", conventionId, nameof(EdgeKind.Registers),
                    Provenance.Convention),
                MakeEdge("name://synthetic/candidate", nameCandidateId, nameof(EdgeKind.ReflectionNameCandidate),
                    Provenance.NameCandidate),
                MakeEdge("hosted://synthetic/service", runtimeUnknownId, nameof(EdgeKind.Registers),
                    Provenance.RuntimeUnknown)
            ]);

            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var conventionEntry = Assert.Single(page.Candidates, c => c.SymbolId == conventionId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, conventionEntry.Status);
            Assert.Equal(DeadCandidateReason.FrameworkConvention, conventionEntry.Reason);
            Assert.Contains("inferred by naming convention", Assert.Single(conventionEntry.Uncertainties).Description);

            var nameCandidateEntry = Assert.Single(page.Candidates, c => c.SymbolId == nameCandidateId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, nameCandidateEntry.Status);
            Assert.Equal(DeadCandidateReason.NameCandidate, nameCandidateEntry.Reason);
            Assert.Contains("matched by name", Assert.Single(nameCandidateEntry.Uncertainties).Description);

            var runtimeUnknownEntry = Assert.Single(page.Candidates, c => c.SymbolId == runtimeUnknownId);
            Assert.Equal(DeadCandidateStatus.Unresolved, runtimeUnknownEntry.Status);
            Assert.Equal(DeadCandidateReason.RuntimeUnknown, runtimeUnknownEntry.Reason);
            Assert.Contains("DeclaredBoundaries.Known", Assert.Single(runtimeUnknownEntry.Uncertainties).Description);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task BindingIncompleteness_Overlap_Unresolved()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Util.cs"] = """
                          namespace TestProject;

                          internal static class Util
                          {
                              internal static void Helper() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var helperId = ResolveSymbolId(snapshotId, "global::TestProject.Util.Helper");
        const string helperFile = "src/TestProject/Util.cs";

        using var store = OpenStore(DbPath);
        try
        {
            // "unsupported_syntax" is one of BindingIncompletenessReason.UnobservableReasons
            // (BindingIncompletenessCollector.cs:30-41) — overlap must force `unresolved`,
            // never `proved_dead`, per the ladder's Q4/binding-incompleteness tier.
            store.SaveBindingIncompleteness(snapshotId,
            [
                new BindingIncompletenessRecord("TestProject", helperFile, "unsupported_syntax", 1, "0.0.0")
            ]);

            var page = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);

            var entry = Assert.Single(page.Candidates, c => c.SymbolId == helperId);
            Assert.Equal(DeadCandidateStatus.Unresolved, entry.Status);
            Assert.Equal(DeadCandidateReason.BindingIncompleteness, entry.Reason);
            Assert.NotEqual(DeadCandidateStatus.ProvedDead, entry.Status);
            var uncertainty = Assert.Single(entry.Uncertainties);
            Assert.Contains("could not be completed because the extractor does not support the relevant syntax",
                uncertainty.Description);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task PublicSurface_ExcludedByDefault_FlaggedOnOptIn()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Api.cs"] = """
                         namespace TestProject;

                         public class Api
                         {
                             public void Endpoint() { }
                         }

                         internal class Caller
                         {
                             internal void Use() { }
                         }
                         """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var endpointId = ResolveSymbolId(snapshotId, "global::TestProject.Api.Endpoint");

        using var store = OpenStore(DbPath);
        try
        {
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);
            Assert.DoesNotContain(defaultPage.Candidates, c => c.SymbolId == endpointId);

            var publicPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, true, false, false, 200, null);
            var entry = Assert.Single(publicPage.Candidates, c => c.SymbolId == endpointId);
            Assert.Equal(DeadCandidateStatus.UncertainDead, entry.Status);
            Assert.Equal(DeadCandidateReason.PublicSurface, entry.Reason);
            Assert.Contains("Public/protected member", Assert.Single(entry.Uncertainties).Description);
            Assert.True(publicPage.UncertainCount >= 1);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task IncludeGenerated_ToggleBehavior()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Gen.g.cs"] = """
                           // <auto-generated/>
                           namespace TestProject;

                           internal class Generated
                           {
                               internal void M() { }
                           }
                           """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var mId = ResolveSymbolId(snapshotId, "global::TestProject.Generated.M");

        using var store = OpenStore(DbPath);
        try
        {
            var defaultPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, false, false, 200, null);
            Assert.DoesNotContain(defaultPage.Candidates, c => c.SymbolId == mId);

            var generatedPage = store.GetDeadCandidatesPage(snapshotId, null, null, null, false, true, false, 200, null);
            var entry = Assert.Single(generatedPage.Candidates, c => c.SymbolId == mId);
            Assert.True(entry.IsGenerated);
            Assert.Equal(DeadCandidateStatus.Uncertain, entry.Status);
            Assert.Equal(DeadCandidateReason.GeneratedExcluded, entry.Reason);
            Assert.Contains("includeGenerated is set to false", Assert.Single(entry.Uncertainties).Description);
        }
        finally
        {
            store.Close();
        }
    }

    [SkippableFact]
    public async Task BatchedQuery_NoPerCandidateGetIncomingEdges()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // Architectural invariant (Q5, lurp-phases-report.md Phase 3): the
        // dead-candidate store must never call IEdgeStore.GetIncomingEdges per
        // candidate — incoming LIVE edges are fetched via one batched
        // `target_symbol_id IN (...)` query per page
        // (DeadCandidateStore.FetchIncomingLiveEdgesBatched), chunked at 900 under
        // SQLITE_MAX_VARIABLE_NUMBER=999. Verified two ways: (1) a static source
        // scan below asserts the per-candidate call site does not exist in
        // DeadCandidateStore.cs; (2) a multi-candidate page still returns correct
        // per-candidate results, so the batching isn't silently dropping rows.
        AssertNoPerCandidateGetIncomingEdgesCallSite();

        var files = new Dictionary<string, string>();
        for (var i = 0; i < 12; i++)
            files[$"Dead{i}.cs"] = $$"""
                                     namespace TestProject;

                                     internal class Dead{{i}}
                                     {
                                         internal void Unused() { }
                                     }
                                     """;
        files["Live.cs"] = """
                           namespace TestProject;

                           internal class LiveTarget
                           {
                               internal void Used() { }
                           }

                           internal class Caller
                           {
                               internal void Call() => new LiveTarget().Used();
                           }
                           """;
        CreateProject("TestProject", files);
        var snapshotId = await RunFullIndexAsync(DbPath);
        var usedId = ResolveSymbolId(snapshotId, "global::TestProject.LiveTarget.Used");

        using var store = OpenStore(DbPath);
        try
        {
            var page = store.GetDeadCandidatesPage(snapshotId, null, null, "Method", false, false, false, 200, null);

            Assert.DoesNotContain(page.Candidates, c => c.SymbolId == usedId);
            for (var i = 0; i < 12; i++)
            {
                var deadId = ResolveSymbolId(snapshotId, $"global::TestProject.Dead{i}.Unused");
                var entry = Assert.Single(page.Candidates, c => c.SymbolId == deadId);
                Assert.Equal(DeadCandidateStatus.ProvedDead, entry.Status);
            }

            Assert.True(page.DeadCount >= 12);
            Assert.Null(page.NextCursor);
        }
        finally
        {
            store.Close();
        }
    }

    private static void AssertNoPerCandidateGetIncomingEdgesCallSite(
        [System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, ".."));
        var storeFile = Path.Combine(repoRoot, "src", "Storage", "DeadCandidateStore.cs");
        Assert.True(File.Exists(storeFile), $"Could not locate {storeFile} for the batching invariant scan.");
        var source = File.ReadAllText(storeFile);
        Assert.DoesNotContain("GetIncomingEdges(", source);
    }
}
