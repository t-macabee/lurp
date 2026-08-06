using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class TimingsHandler
{
    public static void Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
        var asJson = args.Contains("--json");
        var snapshotId = HandlerBootstrap.GetArgValue(args, "--snapshot=");

        if (!File.Exists(dbPath))
        {
            if (asJson)
                HandlerBootstrap.Out.WriteLine(JsonSerializer.Serialize(new { error = "Database not found", database_path = dbPath }, HandlerBootstrap.IndentedJson));
            else
                HandlerBootstrap.Out.WriteLine("Database not found. Run --mode=index first.");
            return;
        }

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            if (snapshotId != null)
            {
                ShowTimingsForSnapshot(store, snapshotId, asJson);
            }
            else
            {
                ShowLatestTimings(store, asJson);
            }
        }
        finally
        {
            store.Close();
        }
    }

    private static void ShowTimingsForSnapshot(IIndexStore store, string snapshotId, bool asJson)
    {
        var timings = store.GetTimings(snapshotId);

        if (timings.Count == 0)
        {
            if (asJson)
                HandlerBootstrap.Out.WriteLine(JsonSerializer.Serialize(new { snapshot_id = snapshotId, timings = Array.Empty<object>(), note = "No timing data for this snapshot." }, HandlerBootstrap.IndentedJson));
            else
                HandlerBootstrap.Out.WriteLine($"No timing data for snapshot {snapshotId}.");
            return;
        }

        if (asJson)
        {
            var output = new
            {
                snapshot_id = snapshotId,
                total_ms = timings.Sum(t => t.ElapsedMs),
                steps = timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs, percent = timings.Sum(x => x.ElapsedMs) > 0 ? Math.Round((double)t.ElapsedMs / timings.Sum(x => x.ElapsedMs) * 100, 1) : 0 })
            };
            HandlerBootstrap.Out.WriteLine(JsonSerializer.Serialize(output, HandlerBootstrap.IndentedJson));
        }
        else
        {
            var totalMs = timings.Sum(t => t.ElapsedMs);
            HandlerBootstrap.Out.WriteLine($"Timings for snapshot {snapshotId}");
            HandlerBootstrap.Out.WriteLine(new string('-', 65));
            HandlerBootstrap.Out.WriteLine($"{"Step",-40} {"Elapsed (ms)",-12} {"%",-6}");
            HandlerBootstrap.Out.WriteLine(new string('-', 65));

            foreach (var t in timings)
            {
                var pct = totalMs > 0 ? (double)t.ElapsedMs / totalMs * 100 : 0;
                HandlerBootstrap.Out.WriteLine($"{t.StepName,-40} {t.ElapsedMs,12} {pct,5:F1}%");
            }
            HandlerBootstrap.Out.WriteLine(new string('-', 65));
            HandlerBootstrap.Out.WriteLine($"{"Total",-40} {totalMs,12}");
        }
    }

    private static void ShowLatestTimings(IIndexStore store, bool asJson)
    {
        var latestSnapshotId = store.GetLatestSnapshotId();
        if (latestSnapshotId == null)
        {
            if (asJson)
                HandlerBootstrap.Out.WriteLine(JsonSerializer.Serialize(new { error = "No snapshots found" }, HandlerBootstrap.IndentedJson));
            else
                HandlerBootstrap.Out.WriteLine("No snapshots found. Run --mode=index first.");
            return;
        }

        ShowTimingsForSnapshot(store, latestSnapshotId, asJson);
    }
}
