namespace Lurp.Handlers;

/// <summary>
///     Shared builder for <c>--mode=timings</c> and MCP <c>lurp_timings</c> (D188.4).
/// </summary>
internal static class TimingsPayload
{
    internal sealed record StepEntry(string step, long elapsed_ms, double percent);

    internal sealed record BuiltTimings(
        long TotalMs,
        IReadOnlyList<StepEntry> Steps,
        IReadOnlyDictionary<string, long> Metrics);

    public static BuiltTimings Build(SqliteIndexStore store, string snapshotId)
    {
        var timings = store.GetTimings(snapshotId);
        var metrics = store.GetMetrics(snapshotId);
        var totalMs = timings.Sum(t => t.ElapsedMs);
        var steps = timings.Select(t => new StepEntry(
            t.StepName,
            t.ElapsedMs,
            totalMs > 0 ? Math.Round((double)t.ElapsedMs / totalMs * 100, 1) : 0)).ToList();
        return new BuiltTimings(totalMs, steps, metrics);
    }
}
