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
        var built = TimingsPayload.Build(store, snapshotId);

        if (asJson)
        {
            var payload = new
            {
                snapshot_id = snapshotId,
                total_ms = built.TotalMs,
                steps = built.Steps,
                metrics = built.Metrics
            };
            output.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.IndentedJson));
            return;
        }

        if (built.Steps.Count == 0)
        {
            output.WriteLine($"No timing data for snapshot {snapshotId}.");
            return;
        }

        output.WriteLine($"Timings for snapshot {snapshotId}");
        output.WriteLine(new string('-', 65));
        output.WriteLine($"{"Step",-40} {"Elapsed (ms)",-12} {"%",-6}");
        output.WriteLine(new string('-', 65));

        foreach (var step in built.Steps)
        {
            var pct = built.TotalMs > 0 ? (double)step.elapsed_ms / built.TotalMs * 100 : 0;
            output.WriteLine($"{step.step,-40} {step.elapsed_ms,12} {pct,5:F1}%");
        }

        output.WriteLine(new string('-', 65));
        output.WriteLine($"{"Total",-40} {built.TotalMs,12}");

        if (built.Metrics.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("Metrics");
            output.WriteLine(new string('-', 65));
            foreach (var (name, value) in built.Metrics)
                output.WriteLine($"{name,-40} {value,12}");
        }
    }
}