using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Handlers;

internal static class StatusHandler
{
    public static async Task Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
        var asJson = args.Contains("--json");

        if (!File.Exists(dbPath))
        {
            ReportNeverIndexed(dbPath, asJson);
            return;
        }

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            store.RunMigrations();
            var schemaVersion = store.GetCurrentSchemaVersion();

            var latestSnapshot = store.LoadLatestSnapshot();
            var latestSnapshotId = latestSnapshot?.SnapshotId;
            if (latestSnapshot == null || latestSnapshotId == null)
            {
                ReportNeverIndexed(dbPath, asJson, schemaVersion, store.GetLatestSnapshotFailure());
                return;
            }

            var solutionPathArg = HandlerBootstrap.GetArgValue(args, "--solution=") ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                ReportSnapshotOnly(store, dbPath, schemaVersion, latestSnapshot, asJson);
                return;
            }

            var freshness = await CheckCurrentWorkspaceAsync(store, solutionPathArg!);
            ReportFreshness(store, dbPath, schemaVersion, latestSnapshot, freshness, asJson);
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

    private static void ReportNeverIndexed(string dbPath, bool asJson, int? schemaVersion = null, SnapshotFailureRow? latestFailure = null)
    {
        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                database_exists = File.Exists(dbPath),
                schema_version = schemaVersion,
                indexed = false,
                latest_failure = latestFailure,
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine("Status: not indexed (no snapshot found). Run --mode=index to create one.");
    }

    private static void ReportSnapshotOnly(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, bool asJson)
    {
        var latestSnapshotId = latestSnapshot.SnapshotId;
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
                manifest = WithBindingCompleteness(store, latestSnapshot),
                latest_failure = store.GetLatestSnapshotFailure(latestSnapshot.WorkspaceId),
            }, JsonOutputOptions));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Schema version: {schemaVersion}");
        Console.WriteLine($"Latest snapshot: {latestSnapshotId}");
        Console.WriteLine("Freshness: unknown — pass --solution=path or set INDEXER_SOLUTION_PATH to compare against the current workspace.");
        ShowTimingIfAvailable(store, latestSnapshotId);
    }

    private static void ReportFreshness(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, WorkspaceFreshness.FreshnessResult freshness, bool asJson)
    {
        var latestSnapshotId = latestSnapshot.SnapshotId;
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
                manifest = WithBindingCompleteness(store, latestSnapshot),
                latest_failure = store.GetLatestSnapshotFailure(latestSnapshot.WorkspaceId),
            }, JsonOutputOptions));
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

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static SnapshotManifest WithBindingCompleteness(SqliteIndexStore store, SnapshotRow snapshot)
    {
        var manifest = SnapshotManifest.FromStorageManifest(snapshot);
        manifest.Completeness?.BindingIncompleteness.AddRange(store.GetBindingIncompleteness(snapshot.SnapshotId));
        return manifest;
    }
}
