using System.Reflection;
using System.Runtime.ExceptionServices;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

public sealed class ContextCancellationTests : IntegrationTestBase
{
    private const string ChainSource = """
                                       namespace TestProject;

                                       public class Chain
                                       {
                                           public void M0() { }
                                           public void M1() => M0();
                                           public void M2() => M1();
                                           public void M3() => M2();
                                           public void M4() => M3();
                                           public void M5() => M4();
                                       }
                                       """;

    [SkippableFact]
    public async Task Capsule_CancelledInsideTier_StopsEarly()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var snapshotId = await IndexChainAsync();
        var typeId = ResolveSymbolId(snapshotId, "global::TestProject.Chain");
        var lookup = new ContextLookup(snapshotId, typeId, null, null);
        var options = new ContextAssemblyOptions(ContextIntent.Inspect, 8000, 5);

        using var store = OpenStore(DbPath);

        var (fullEdges, fullProbe) = NewProbe(store, null);
        var capsule = ContextAssembler.ResolveAndAssemble(fullEdges, store, lookup, options, store, store, store, CancellationToken.None);
        var fullReads = fullProbe.Reads;

        Assert.NotNull(capsule);
        Assert.True(fullReads >= 10, $"The uncancelled assembly made {fullReads} edge reads; the fixture needs at least 10 to show depth.");

        using var cts = new CancellationTokenSource();
        var (cancelledEdges, cancelledProbe) = NewProbe(store, cts);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ContextAssembler.ResolveAndAssemble(cancelledEdges, store, lookup, options, store, store, store, cts.Token));

        Assert.True(cancelledProbe.Reads * 4 <= fullReads,
            $"The cancelled assembly made {cancelledProbe.Reads} edge reads; the uncancelled one made {fullReads}. The cancelled run must stop early.");
    }

    [SkippableFact]
    public async Task Tier_CancelledInsideTier_StopsEarly()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var snapshotId = await IndexChainAsync();
        var typeId = SymbolId.Parse(ResolveSymbolId(snapshotId, "global::TestProject.Chain"));

        using var store = OpenStore(DbPath);
        var failures = new List<string>();

        foreach (var tier in ContextAssembler.TierNames)
        {
            var (fullEdges, fullProbe) = NewProbe(store, null);
            ContextAssembler.BuildTierPage(fullEdges, store, snapshotId, typeId, tier, 5, false, 0, 100, CancellationToken.None);
            var fullReads = fullProbe.Reads;

            using var cts = new CancellationTokenSource();
            var (cancelledEdges, cancelledProbe) = NewProbe(store, cts);
            var threw = false;
            try
            {
                ContextAssembler.BuildTierPage(cancelledEdges, store, snapshotId, typeId, tier, 5, false, 0, 100, cts.Token);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            if (!threw || cancelledProbe.Reads >= fullReads)
                failures.Add($"{tier} (full reads {fullReads}, cancelled reads {cancelledProbe.Reads}, threw {threw})");
        }

        Assert.True(failures.Count == 0,
            "Tiers that did not stop early when cancelled: " + string.Join("; ", failures));
    }

    [SkippableFact]
    public async Task Capsule_CancelledInsideContractSections_Throws()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var snapshotId = await IndexChainAsync();
        var typeId = ResolveSymbolId(snapshotId, "global::TestProject.Chain");
        var lookup = new ContextLookup(snapshotId, typeId, null, null);
        var options = new ContextAssemblyOptions(ContextIntent.Inspect, 8000, 5);

        using var store = OpenStore(DbPath);

        using var controlCts = new CancellationTokenSource();
        var control = new CancelOnUseSemanticDiffStore(store, controlCts);
        var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store, control, CancellationToken.None);
        Assert.NotNull(capsule);
        Assert.True(control.Calls >= 1, "The semantic diff store was never reached; the test cannot prove the contract sections are checked.");

        using var cts = new CancellationTokenSource();
        var cancelling = new CancelOnUseSemanticDiffStore(store, cts);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store, cancelling, cts.Token));
    }

    [SkippableFact]
    public async Task Uncertainties_CancelledInsideNeighborhoodLoops_StopsEarly()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var snapshotId = await IndexChainAsync();
        var typeId = ResolveSymbolId(snapshotId, "global::TestProject.Chain");
        var lookup = new ContextLookup(snapshotId, typeId, null, null);
        var options = new ContextAssemblyOptions(ContextIntent.Inspect, 8000, 5);
        var symbolId = SymbolId.Parse(typeId);

        using var store = OpenStore(DbPath);

        var (fullEdges, fullProbe) = NewProbe(store, null);
        var fullCapsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store, store);
        new UncertaintyDetector(fullEdges, store, snapshotId, symbolId, false, null, [], CancellationToken.None)
            .PopulateUncertainties(fullCapsule);
        var fullReads = fullProbe.Reads;

        Assert.True(fullReads >= 20, $"The uncancelled uncertainty stage made {fullReads} edge reads; the fixture needs at least 20 to show depth.");

        var failures = new List<string>();

        // Every cancel point is tried: a loop without a check leaks only for the k values inside that loop (R49 sample points missed one).
        for (var k = 1; k <= fullReads; k++)
        {
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store, store);
            using var cts = new CancellationTokenSource();
            var (cancelledEdges, cancelledProbe) = NewProbe(store, cts);
            cancelledProbe.CancelAtRead = k;
            var threw = false;
            try
            {
                new UncertaintyDetector(cancelledEdges, store, snapshotId, symbolId, false, null, [], cts.Token)
                    .PopulateUncertainties(capsule);
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            if (!threw || cancelledProbe.Reads - k > 2)
                failures.Add($"k={k} (reads {cancelledProbe.Reads}, threw {threw})");
        }

        Assert.True(failures.Count == 0,
            $"The uncertainty stage did not stop within two reads of a cancel at every cancel point from 1 to {fullReads} (uncancelled reads {fullReads}): " + string.Join("; ", failures));
    }

    [SkippableFact]
    public async Task SuggestedVerification_PreCancelledToken_Throws()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");
        var snapshotId = await IndexChainAsync();
        var typeId = ResolveSymbolId(snapshotId, "global::TestProject.Chain");
        var lookup = new ContextLookup(snapshotId, typeId, null, null);
        var options = new ContextAssemblyOptions(ContextIntent.Inspect, 8000, 5);
        var symbolId = SymbolId.Parse(typeId);

        using var store = OpenStore(DbPath);
        var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store, store);

        // Depth is not testable on the chain fixture: it has no TestedBy edge, so the method makes one read.
        new UncertaintyDetector(store, store, snapshotId, symbolId, false, null, [], CancellationToken.None)
            .PopulateSuggestedVerification(capsule);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new UncertaintyDetector(store, store, snapshotId, symbolId, false, null, [], cancelled.Token)
                .PopulateSuggestedVerification(capsule));
    }

    private async Task<string> IndexChainAsync()
    {
        CreateProject("TestProject", new Dictionary<string, string> { ["Chain.cs"] = ChainSource });
        return await RunFullIndexAsync(DbPath);
    }

    private static (IEdgeStore Edges, EdgeReadProbe Probe) NewProbe(IEdgeStore target, CancellationTokenSource? cancelSource)
    {
        var edges = DispatchProxy.Create<IEdgeStore, EdgeReadProbe>();
        var probe = (EdgeReadProbe)edges;
        probe.Target = target;
        probe.CancelSource = cancelSource;
        return (edges, probe);
    }

    /// <summary>
    ///     Forwards every <see cref="IEdgeStore" /> call to the target and counts the
    ///     <c>Get*</c> reads. Read number <see cref="CancelAtRead" /> (1 by default) cancels
    ///     <see cref="CancelSource" />, so a check that follows that read must throw.
    /// </summary>
    public class EdgeReadProbe : DispatchProxy
    {
        public IEdgeStore Target { get; set; } = null!;

        public CancellationTokenSource? CancelSource { get; set; }

        public int CancelAtRead { get; set; } = 1;

        public int Reads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name.StartsWith("Get", StringComparison.Ordinal) && ++Reads == CancelAtRead)
                CancelSource?.Cancel();

            try
            {
                return targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
                throw;
            }
        }
    }

    /// <summary>
    ///     Forwards to the inner store and cancels <see cref="Cancellation" /> on the first
    ///     <see cref="GetSemanticChangesToSnapshot" /> call.
    /// </summary>
    public sealed class CancelOnUseSemanticDiffStore(ISemanticDiffStore inner, CancellationTokenSource cancellation) : ISemanticDiffStore
    {
        public int Calls { get; private set; }

        public void SaveSemanticChanges(string fromSnapshotId, string toSnapshotId, IEnumerable<SemanticChange> changes)
        {
            inner.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes);
        }

        public List<SemanticChange> GetSemanticChanges(string fromSnapshotId, string toSnapshotId)
        {
            return inner.GetSemanticChanges(fromSnapshotId, toSnapshotId);
        }

        public List<SemanticChange> GetSemanticChangesToSnapshot(string toSnapshotId)
        {
            Calls++;
            cancellation.Cancel();
            return inner.GetSemanticChangesToSnapshot(toSnapshotId);
        }
    }
}
