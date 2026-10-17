using System.Text.Json;

namespace Lurp.Handlers;

internal static class TimingsHandler
{
    public static void Run(string[] args)
    {
        var asJson = args.Contains("--json");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="),
            (store, snapshotId) => ShowTimingsForSnapshot(store, snapshotId, asJson));
    }

    private static void ShowTimingsForSnapshot(SqliteIndexStore store, string snapshotId, bool asJson)
    {
        var timings = store.GetTimings(snapshotId);
        var metrics = store.GetMetrics(snapshotId);

        if (timings.Count == 0)
        {
            Console.WriteLine(asJson
                ? JsonSerializer.Serialize(new { snapshot_id = snapshotId, timings = Array.Empty<object>(), note = "No timing data for this snapshot." }, HandlerBootstrap.IndentedJson)
                : $"No timing data for snapshot {snapshotId}.");
            return;
        }

        var timingTotalMs = timings.Sum(t => t.ElapsedMs);

        if (asJson)
        {
            var output = new
            {
                snapshot_id = snapshotId,
                total_ms = timingTotalMs,
                steps = timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs, percent = timingTotalMs > 0 ? Math.Round((double)t.ElapsedMs / timingTotalMs * 100, 1) : 0 }),
                metrics
            };
            Console.WriteLine(JsonSerializer.Serialize(output, HandlerBootstrap.IndentedJson));
        }
        else
        {
            Console.WriteLine($"Timings for snapshot {snapshotId}");
            Console.WriteLine(new string('-', 65));
            Console.WriteLine($"{"Step",-40} {"Elapsed (ms)",-12} {"%",-6}");
            Console.WriteLine(new string('-', 65));

            foreach (var t in timings)
            {
                var pct = timingTotalMs > 0 ? (double)t.ElapsedMs / timingTotalMs * 100 : 0;
                Console.WriteLine($"{t.StepName,-40} {t.ElapsedMs,12} {pct,5:F1}%");
            }

            Console.WriteLine(new string('-', 65));
            Console.WriteLine($"{"Total",-40} {timingTotalMs,12}");

            if (metrics.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Metrics");
                Console.WriteLine(new string('-', 65));
                foreach (var (name, value) in metrics)
                    Console.WriteLine($"{name,-40} {value,12}");
            }
        }
    }
}