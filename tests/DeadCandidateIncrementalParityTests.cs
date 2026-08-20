using Lurp.Storage;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
///     Phase 4 verification (<c>lurp-phases-report.md</c> / <c>lurp-item-7-dead-code-tasklist.md:180</c>):
///     dead-candidate results must converge between an incremental snapshot and a
///     clean full rebuild of the same content, mirroring the existing
///     <see cref="IncrementalParityTests" /> matrix (investigation §3 I5). This
///     goes one step further than <see cref="SnapshotAssertions.CompareSnapshotsAreEquivalent" />
///     (which pins raw symbols/edges/declarations) by also comparing the derived
///     <see cref="DeadCandidateStore.GetDeadCandidatesPage" /> output, including
///     status/reason per candidate — the layer Phase 3 added on top of the raw
///     graph.
/// </summary>
public sealed class DeadCandidateIncrementalParityTests : IntegrationTestBase
{
    private readonly string _cleanRebuildDbPath;

    public DeadCandidateIncrementalParityTests()
    {
        _cleanRebuildDbPath = Path.Combine(TestDir, "clean.db");
    }

    private static List<string> DeadCandidateFacts(DeadCandidatePage page)
    {
        return
        [
            .. page.Candidates
                .Select(c => string.Join("|", c.SymbolId, c.Status, c.Reason))
                .OrderBy(f => f, StringComparer.Ordinal)
        ];
    }

    private void AssertDeadCandidatesConverge(string snapshotB, string snapshotC)
    {
        using var storeB = OpenStore(DbPath);
        using var storeC = OpenStore(_cleanRebuildDbPath);
        try
        {
            var pageB = storeB.GetDeadCandidatesPage(snapshotB, null, null, null, true, false, false, 500, null);
            var pageC = storeC.GetDeadCandidatesPage(snapshotC, null, null, null, true, false, false, 500, null);

            Assert.Equal(pageC.CandidateCount, pageB.CandidateCount);
            Assert.Equal(pageC.DeadCount, pageB.DeadCount);
            Assert.Equal(pageC.UncertainCount, pageB.UncertainCount);
            Assert.Equal(pageC.UnresolvedCount, pageB.UnresolvedCount);
            Assert.Equal(DeadCandidateFacts(pageC), DeadCandidateFacts(pageB));
        }
        finally
        {
            storeB.Close();
            storeC.Close();
        }
    }

    [SkippableFact]
    public async Task Parity_AddNewDeadInternalMethod_ConvergesBetweenIncrementalAndFullRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Calculator.cs"] = """
                                namespace TestProject;

                                public class Calculator
                                {
                                    public int Add(int a, int b) => a + b;

                                    internal void DeadHelper() { }
                                }
                                """,
            ["Service.cs"] = """
                             namespace TestProject;

                             public class Service
                             {
                                 public int Compute(int x, int y) => new Calculator().Add(x, y);
                             }
                             """
        });
        var snapshotA = await RunFullIndexAsync(DbPath);

        // V2 adds a second never-called internal method — a brand-new dead
        // candidate that only exists after the incremental edit.
        WriteFile("TestProject", "Calculator.cs", """
                                                   namespace TestProject;

                                                   public class Calculator
                                                   {
                                                       public int Add(int a, int b) => a + b;

                                                       internal void DeadHelper() { }

                                                       internal void AnotherDeadHelper() { }
                                                   }
                                                   """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        AssertDeadCandidatesConverge(snapshotB, snapshotC);

        // Sanity: the new dead candidate is actually present and proved_dead in
        // both the incremental snapshot and the clean rebuild, not just absent
        // from both by coincidence.
        var anotherId = ResolveSymbolId(snapshotB, "global::TestProject.Calculator.AnotherDeadHelper");
        using var storeB = OpenStore(DbPath);
        try
        {
            var pageB = storeB.GetDeadCandidatesPage(snapshotB, null, null, null, true, false, false, 500, null);
            var entry = Assert.Single(pageB.Candidates, c => c.SymbolId == anotherId);
            Assert.Equal(DeadCandidateStatus.ProvedDead, entry.Status);
        }
        finally
        {
            storeB.Close();
        }
    }

    [SkippableFact]
    public async Task Parity_AccessibilityFlip_ReclassifiesLadderTierConsistently()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        // V1: Endpoint is public and uncalled -> excluded from default proved_dead
        // (Q1). V2 flips it to internal, uncalled -> proved_dead by default. The
        // ladder-tier reclassification driven by a pure accessibility edit must
        // land the same way whether reached incrementally or via a clean rebuild.
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Api.cs"] = """
                         namespace TestProject;

                         public class Api
                         {
                             public void Endpoint() { }
                         }
                         """
        });
        var snapshotA = await RunFullIndexAsync(DbPath);

        WriteFile("TestProject", "Api.cs", """
                                           namespace TestProject;

                                           public class Api
                                           {
                                               internal void Endpoint() { }
                                           }
                                           """);

        var snapshotB = await RunIncrementalIndexAsync();
        var snapshotC = await RunFullIndexAsync(_cleanRebuildDbPath);

        Assert.NotEqual(snapshotA, snapshotB);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
        AssertDeadCandidatesConverge(snapshotB, snapshotC);

        var endpointId = ResolveSymbolId(snapshotB, "global::TestProject.Api.Endpoint");
        using var storeB = OpenStore(DbPath);
        try
        {
            var defaultPage = storeB.GetDeadCandidatesPage(snapshotB, null, null, null, false, false, false, 500, null);
            var entry = Assert.Single(defaultPage.Candidates, c => c.SymbolId == endpointId);
            Assert.Equal(DeadCandidateStatus.ProvedDead, entry.Status);
            Assert.Equal(DeadCandidateReason.NoIncomingLiveEdges, entry.Reason);
        }
        finally
        {
            storeB.Close();
        }
    }

}
