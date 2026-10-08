using System.Diagnostics;

namespace Lurp.Workspace;

/// <summary>
///     Planner statistics are an optimization: a failure warns and never fails or
///     re-marks the complete snapshot.
/// </summary>
internal static class IndexFinalization
{
    internal static void RefreshPlannerStatistics(IStoreConnection store, IOutputSink output, List<SnapshotTimingRow> timings)
    {
        var swStats = Stopwatch.StartNew();
        try
        {
            store.RefreshPlannerStatistics();
        }
        catch (Exception ex)
        {
            output.WriteErrorLine($"WARNING: Failed to refresh planner statistics: {ex.Message}");
        }
        swStats.Stop();
        timings.Add(new SnapshotTimingRow(SnapshotTimingSteps.PlannerStatistics, swStats.ElapsedMilliseconds));
    }
}
