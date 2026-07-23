using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Handlers;

internal static class StatusHandler
{
    public static async Task Run(string[] args)
    {
        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg!), "index.db");
        var asJson = args.Contains("--json");

        if (!File.Exists(dbPath))
        {
            ReportNeverIndexed(dbPath, asJson);
            return;
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            store.RunMigrations();
            var schemaVersion = store.GetCurrentSchemaVersion();

            var latestSnapshotId = store.GetLatestSnapshotId();
            if (latestSnapshotId == null)
            {
                ReportNeverIndexed(dbPath, asJson, schemaVersion);
                return;
            }

            var solutionPathArg = GetArgValue(args, "--solution=") ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                ReportSnapshotOnly(store, dbPath, schemaVersion, latestSnapshotId, asJson);
                return;
            }

            var freshness = await CheckCurrentWorkspaceAsync(store, solutionPathArg!);
            ReportFreshness(store, dbPath, schemaVersion, latestSnapshotId, freshness, asJson);
        }
        finally
        {
            store.Close();
        }
    }

    private static async Task<WorkspaceFreshness.FreshnessResult> CheckCurrentWorkspaceAsync(ISnapshotStore store, string solutionPath)
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        return WorkspaceFreshness.CheckFreshness(workspaceInfo, store);
    }

    private static void ReportNeverIndexed(string dbPath, bool asJson, int? schemaVersion = null)
    {
        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                database_exists = File.Exists(dbPath),
                schema_version = schemaVersion,
                indexed = false,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine("Status: not indexed (no snapshot found). Run --mode=index to create one.");
    }

    private static void ReportSnapshotOnly(SqliteIndexStore store, string dbPath, int schemaVersion, string latestSnapshotId, bool asJson)
    {
        if (asJson)
        {
            List<SnapshotTimingRow>? timings = null;
            try { timings = store.GetTimings(latestSnapshotId); }
            catch { }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                schema_version = schemaVersion,
                latest_snapshot_id = latestSnapshotId,
                freshness_checked = false,
                note = "Pass --solution=path or set INDEXER_SOLUTION_PATH to check freshness against the current workspace.",
                timing_summary = timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms = timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : (long?)null,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Schema version: {schemaVersion}");
        Console.WriteLine($"Latest snapshot: {latestSnapshotId}");
        Console.WriteLine("Freshness: unknown — pass --solution=path or set INDEXER_SOLUTION_PATH to compare against the current workspace.");
        ShowTimingIfAvailable(store, latestSnapshotId);
    }

    private static void ReportFreshness(SqliteIndexStore store, string dbPath, int schemaVersion, string latestSnapshotId, WorkspaceFreshness.FreshnessResult freshness, bool asJson)
    {
        if (asJson)
        {
            List<SnapshotTimingRow>? timings = null;
            try { timings = store.GetTimings(latestSnapshotId); }
            catch { }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                schema_version = schemaVersion,
                latest_snapshot_id = latestSnapshotId,
                is_fresh = freshness.IsFresh,
                mismatches = freshness.Mismatches.Select(m => new
                {
                    kind = m.Kind.ToString(),
                    description = m.Description,
                    document = m.Document?.ToString(),
                    detail = m.Detail,
                }),
                timing_summary = timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms = timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : (long?)null,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Schema version: {schemaVersion}");
        Console.WriteLine($"Latest snapshot: {latestSnapshotId}");
        Console.WriteLine(freshness.IsFresh ? "Freshness: up to date." : $"Freshness: stale ({freshness.Mismatches.Count} mismatch(es)).");

        foreach (var mismatch in freshness.Mismatches)
        {
            Console.WriteLine($"  [{mismatch.Kind}] {mismatch.Description}");
        }

        ShowTimingIfAvailable(store, latestSnapshotId);
    }

    private static void ShowTimingIfAvailable(SqliteIndexStore store, string snapshotId)
    {
        try
        {
            var timings = store.GetTimings(snapshotId);

            if (timings.Count == 0) return;

            var totalMs = timings.Sum(t => t.ElapsedMs);
            Console.WriteLine($"Timing summary ({totalMs} ms total):");
            foreach (var t in timings)
            {
                Console.WriteLine($"  {t.StepName}: {t.ElapsedMs} ms");
            }
        }
        catch
        {
            // Timings are optional; silently skip on error
        }
    }

    private static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }
}
