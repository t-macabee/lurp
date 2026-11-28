using System.Diagnostics;

namespace Lurp.Workspace;

/// <summary>
///     Shared semantic-diff step used by both the full-rebuild
///     (<see cref="IndexRunner" />) and incremental (<see cref="IncrementalIndexer" />) pipelines.
///     Only the block itself is shared — each pipeline keeps its own phase order and decides
///     when to call this.
/// </summary>
internal static class SemanticDiffStep
{
    public static void ComputeAndPersist(
        SqliteIndexStore store,
        IOutputSink output,
        string fromSnapshotId,
        string toSnapshotId,
        HashSet<string>? changedSymbolIds,
        List<SnapshotTimingRow> timings)
    {
        Run(store, output, fromSnapshotId, toSnapshotId, targetToSnapshotId: null, changedSymbolIds, timings,
            changes => store.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes));
    }

    /// <summary>
    ///     Diffs a staged rebuild as its target snapshot. The caller passes the result to
    ///     <see cref="SqliteIndexStore.PromoteAndCompleteIndex" />, which writes it.
    /// </summary>
    public static IReadOnlyList<SemanticChange> ComputeForRebuild(
        SqliteIndexStore store,
        IOutputSink output,
        string fromSnapshotId,
        string stagingSnapshotId,
        string targetToSnapshotId,
        HashSet<string>? changedSymbolIds,
        List<SnapshotTimingRow> timings)
    {
        return Run(store, output, fromSnapshotId, stagingSnapshotId, targetToSnapshotId, changedSymbolIds, timings, persist: null);
    }

    private static List<SemanticChange> Run(
        SqliteIndexStore store,
        IOutputSink output,
        string fromSnapshotId,
        string toSnapshotId,
        string? targetToSnapshotId,
        HashSet<string>? changedSymbolIds,
        List<SnapshotTimingRow> timings,
        Action<IReadOnlyList<SemanticChange>>? persist)
    {
        var sw = Stopwatch.StartNew();
        output.Write("Computing semantic diff from previous snapshot... ");

        var differ = new SemanticDiffer(store, store, store);
        var (diffChanges, skippedComparisons) = changedSymbolIds != null
            ? differ.ComputeDiff(fromSnapshotId, toSnapshotId, changedSymbolIds, targetToSnapshotId)
            : differ.ComputeDiff(fromSnapshotId, toSnapshotId, targetToSnapshotId);

        persist?.Invoke(diffChanges);

        output.WriteLine($"done ({diffChanges.Count} changes, {skippedComparisons} comparisons skipped).");
        sw.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.SemanticDiff, sw.ElapsedMilliseconds));
        return diffChanges;
    }
}
