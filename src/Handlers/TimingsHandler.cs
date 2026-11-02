using System.Text.Json;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class TimingsHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        var asJson = args.Contains("--json");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="),
            (store, snapshotId) => ShowTimingsForSnapshot(store, snapshotId, asJson, output), output);
    }

    private static void ShowTimingsForSnapshot(SqliteIndexStore store, string snapshotId, bool asJson, IOutputSink output)
    {
        var timings = store.GetTimings(snapshotId);
        var metrics = store.GetMetrics(snapshotId);

        if (timings.Count == 0)
        {
            output.WriteLine(asJson
                ? JsonSerializer.Serialize(new { snapshot_id = snapshotId, timings = Array.Empty<object>(), note = "No timing data for this snapshot." }, HandlerBootstrap.IndentedJson)
                : $"No timing data for snapshot {snapshotId}.");
            return;
        }

        var timingTotalMs = timings.Sum(t => t.ElapsedMs);

        if (asJson)
        {
            var payload = new
            {
                snapshot_id = snapshotId,
                total_ms = timingTotalMs,
                steps = timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs, percent = timingTotalMs > 0 ? Math.Round((double)t.ElapsedMs / timingTotalMs * 100, 1) : 0 }),
                metrics
            };
            output.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.IndentedJson));
        }
        else
        {
            output.WriteLine($"Timings for snapshot {snapshotId}");
            output.WriteLine(new string('-', 65));
            output.WriteLine($"{"Step",-40} {"Elapsed (ms)",-12} {"%",-6}");
            output.WriteLine(new string('-', 65));

            foreach (var t in timings)
            {
                var pct = timingTotalMs > 0 ? (double)t.ElapsedMs / timingTotalMs * 100 : 0;
                output.WriteLine($"{t.StepName,-40} {t.ElapsedMs,12} {pct,5:F1}%");
            }

            output.WriteLine(new string('-', 65));
            output.WriteLine($"{"Total",-40} {timingTotalMs,12}");

            if (metrics.Count > 0)
            {
                output.WriteLine();
                output.WriteLine("Metrics");
                output.WriteLine(new string('-', 65));
                foreach (var (name, value) in metrics)
                    output.WriteLine($"{name,-40} {value,12}");
            }
        }
    }
}