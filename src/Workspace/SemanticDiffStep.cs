using System.Diagnostics;

namespace Lurp.Workspace;

/// <summary>
///     Shared semantic-diff-and-persist step used by both the full-rebuild
///     (<see cref="IndexRunner" />) and incremental (<see cref="IncrementalIndexer" />) pipelines.
///     Only the block itself is shared — each pipeline keeps its own phase order and decides
///     when to call this.
/// </summary>
internal static class SemanticDiffStep
{
    public static void ComputeAndPersist(
        IIndexStore store,
        IOutputSink output,
        string fromSnapshotId,
        string toSnapshotId,
        HashSet<string>? changedSymbolIds,
        List<SnapshotTimingRow> timings)
    {
        var sw = Stopwatch.StartNew();
        output.Write("Computing semantic diff from previous snapshot... ");

        var differ = new SemanticDiffer(store, store, store, store);
        var (diffChanges, skippedComparisons) = changedSymbolIds != null
            ? differ.ComputeDiff(fromSnapshotId, toSnapshotId, changedSymbolIds)
            : differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        store.SaveSemanticChanges(fromSnapshotId, toSnapshotId, diffChanges);

        output.WriteLine($"done ({diffChanges.Count} changes, {skippedComparisons} comparisons skipped).");
        sw.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.SemanticDiff, sw.ElapsedMilliseconds, DateTime.UtcNow));
    }
}