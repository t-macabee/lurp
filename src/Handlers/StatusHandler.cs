using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Handlers;

internal static class StatusHandler
{
    /// <summary>
    /// Status renders one document, not a sequence, so it accepts the same
    /// <c>--output=summary|json</c> vocabulary other read commands use (jsonl is rejected,
    /// same as <c>--mode=context</c> for its non-tier path). <c>--json</c> is kept working
    /// as a back-compat alias so existing callers are unaffected; the historical default
    /// (neither flag given) stays the human-readable summary text.
    /// </summary>
    private static bool ResolveAsJson(string[] args)
    {
        var outputRaw = HandlerBootstrap.GetArgValue(args, "--output=");
        if (string.IsNullOrEmpty(outputRaw))
            return args.Contains("--json");

        switch (outputRaw.ToLowerInvariant())
        {
            case "json":
                return true;
            case "summary":
                return false;
            case "jsonl":
                HandlerBootstrap.Fail("ERROR: --output=jsonl is not supported for this mode; its payload is a single document. Use --output=json or --output=summary.");
                return false;
            default:
                HandlerBootstrap.Fail("ERROR: --output must be one of: summary, json.");
                return false;
        }
    }

    public static async Task Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
        var asJson = ResolveAsJson(args);

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

            var includeDocuments = WantsDetail(args, "documents");
            var includeCompleteness = WantsDetail(args, "completeness");
            var solutionPathArg = HandlerBootstrap.GetArgValue(args, "--solution=")
                ?? Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH")
                ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                ReportSnapshotOnly(store, dbPath, schemaVersion, latestSnapshot, asJson, includeDocuments, includeCompleteness);
                return;
            }

            var freshness = await CheckCurrentWorkspaceAsync(store, solutionPathArg!);
            ReportFreshness(store, dbPath, schemaVersion, latestSnapshot, freshness, asJson, includeDocuments, includeCompleteness);
        }
        finally
        {
            store.Close();
        }
    }

    private static async Task<WorkspaceFreshness.FreshnessResult> CheckCurrentWorkspaceAsync(ISnapshotManifestStore manifests, string solutionPath)
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        return WorkspaceFreshness.CheckFreshness(workspaceInfo, manifests);
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
            }, HandlerBootstrap.IndentedJson));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine("Status: not indexed (no snapshot found). Run --mode=index to create one.");
    }

    private static void ReportSnapshotOnly(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, bool asJson, bool includeDocuments, bool includeCompleteness)
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
                note = "Pass --solution=path or set LURP_SOLUTION_PATH to check freshness against the current workspace (INDEXER_SOLUTION_PATH accepted for back-compat).",
                timing_summary = timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms = timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : (long?)null,
                manifest = ManifestJson(WithBindingCompleteness(store, latestSnapshot, includeCompleteness), includeDocuments),
                latest_failure = store.GetLatestSnapshotFailure(latestSnapshot.WorkspaceId),
            }, JsonOutputOptions));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Schema version: {schemaVersion}");
        Console.WriteLine($"Latest snapshot: {latestSnapshotId}");
        Console.WriteLine("Freshness: unknown : pass --solution=path or set LURP_SOLUTION_PATH to compare against the current workspace (INDEXER_SOLUTION_PATH accepted for back-compat).");
        ShowTimingIfAvailable(store, latestSnapshotId);
    }

    private static void ReportFreshness(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, WorkspaceFreshness.FreshnessResult freshness, bool asJson, bool includeDocuments, bool includeCompleteness)
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
                manifest = ManifestJson(WithBindingCompleteness(store, latestSnapshot, includeCompleteness), includeDocuments),
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

    private static SnapshotManifest WithBindingCompleteness(SqliteIndexStore store, SnapshotRow snapshot, bool includeDetail)
    {
        var manifest = SnapshotManifest.FromStorageManifest(snapshot);
        var records = store.GetBindingIncompleteness(snapshot.SnapshotId);
        manifest.Completeness = manifest.Completeness?.WithBindingIncompleteness(records, includeDetail);
        return manifest;
    }

    /// <summary>
    /// True when <c>--detail=</c> names <paramref name="section"/> (comma-separated, or
    /// <c>all</c>).
    /// </summary>
    private static bool WantsDetail(string[] args, string section)
    {
        var raw = HandlerBootstrap.GetArgValue(args, "--detail=");
        if (string.IsNullOrEmpty(raw))
            return false;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(part, section, StringComparison.OrdinalIgnoreCase)
                      || string.Equals(part, "all", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Renders the manifest for JSON output, replacing the per-document version map with
    /// its count unless <c>--detail=documents</c> asks for it.
    ///
    /// The map is the single largest thing in <c>status --json</c> (196 entries on the
    /// self-host solution) and is almost never what the caller wanted. It is *summarized*,
    /// not dropped: <c>documentCount</c> takes its place, so the output never silently
    /// looks like a snapshot with no documents. Serializing through a node keeps every
    /// other manifest field bound to the model rather than to a hand-copied field list
    /// that would drift the first time the manifest gains a property.
    /// </summary>
    private static JsonNode? ManifestJson(SnapshotManifest manifest, bool includeDocuments)
    {
        var node = JsonSerializer.SerializeToNode(manifest, JsonOutputOptions);
        if (includeDocuments || node is not JsonObject obj)
            return node;

        var documentCount = (obj["document_versions"] as JsonObject)?.Count ?? 0;
        obj.Remove("document_versions");
        obj["document_count"] = documentCount;
        obj["documents_note"] = "Per-document versions omitted; pass --detail=documents to include them.";
        return obj;
    }
}
