using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class StatusHandler
{
    internal const int DefaultMaxMismatches = 50;

    public static async Task Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");

        // Status renders one document, not a sequence, so it shares the same
        // --output=summary|json vocabulary the other read commands use (jsonl is
        // rejected, same as --mode=context for its non-tier path). Route it through
        // the single ParseOutputMode chokepoint so the vocabulary and its error text
        // cannot drift from the read commands. --json stays a back-compat alias for
        // --output=json; the historical default (neither flag given) stays the
        // human-readable summary text.
        var asJson = HandlerBootstrap.ParseOutputMode(
                args,
                allowJsonl: false,
                defaultMode: args.Contains("--json") ? OutputMode.Json : OutputMode.Summary)
            == OutputMode.Json;

        // Parse --detail= once, before any database access, so a bad value fails the
        // same way whatever the database state (B51). An empty or missing value means
        // no extra sections.
        var detailSections = default(StatusSectionSet);
        var detailArg = HandlerBootstrap.GetArgValue(args, "--detail=");
        if (!string.IsNullOrWhiteSpace(detailArg)
            && !StatusSections.TryParse(detailArg, allowSurfaceNames: false, out detailSections, out var badDetail))
            HandlerBootstrap.Fail($"ERROR: --detail: unknown value '{badDetail}'. Use {StatusSections.Vocabulary(false)}.");

        if (!File.Exists(dbPath))
        {
            ReportNeverIndexed(dbPath, asJson);
            return;
        }

        var store = HandlerBootstrap.OpenReadStoreUnchecked(dbPath);

        try
        {
            var schemaVersion = store.GetCurrentSchemaVersion();
            if (schemaVersion != VersionConstants.DatabaseSchemaVersion)
            {
                ReportSchemaMismatch(dbPath, schemaVersion, asJson);
                return;
            }

            var latestSnapshot = store.LoadLatestSnapshot();
            var latestSnapshotId = latestSnapshot?.SnapshotId;
            if (latestSnapshot == null || latestSnapshotId == null)
            {
                ReportNeverIndexed(dbPath, asJson, schemaVersion, store.GetLatestSnapshotFailure());
                return;
            }

            var includeDocuments = detailSections.Documents;
            var includeReferences = detailSections.References;
            var includeCompleteness = detailSections.Completeness;
            var maxMismatches = ParseMaxArg(args, "--max-mismatches=", DefaultMaxMismatches);
            var solutionPathArg = HandlerBootstrap.GetArgValue(args, "--solution=")
                                  ?? Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                ReportSnapshotOnly(store, dbPath, schemaVersion, latestSnapshot, asJson, includeDocuments, includeReferences, includeCompleteness);
                return;
            }

            var freshness = await CheckCurrentWorkspaceAsync(store, solutionPathArg!);
            ReportFreshness(store, dbPath, schemaVersion, latestSnapshot, freshness, asJson, includeDocuments, includeReferences, includeCompleteness, maxMismatches);
        }
        finally
        {
            store.Close();
        }
    }

    private static async Task<WorkspaceFreshness.FreshnessResult> CheckCurrentWorkspaceAsync(SqliteIndexStore store, string solutionPath)
    {
        if (!MSBuildLocator.IsRegistered) MSBuildRegistration.RegisterOrThrow();

        using var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(solutionPath));
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        return WorkspaceFreshness.CheckFreshness(workspaceInfo, store);
    }

    private static void ReportSchemaMismatch(string dbPath, int actualVersion, bool asJson)
    {
        var expectedVersion = VersionConstants.DatabaseSchemaVersion;
        var explanation = actualVersion < expectedVersion
            ? $"Index database at {dbPath} uses schema v{actualVersion}; this Lurp needs v{expectedVersion}. Run 'lurp --mode=index' to update it."
            : $"Index database at {dbPath} uses schema v{actualVersion}, newer than this Lurp (v{expectedVersion}). Update Lurp.";

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                database_exists = File.Exists(dbPath),
                schema_version = actualVersion,
                schema_version_expected = expectedVersion,
                schema_version_mismatch = true,
                contract_version = VersionConstants.CliMcpContractVersion,
                schema_version_note = explanation
            }, HandlerBootstrap.IndentedJson));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Schema version: {actualVersion} (expected {expectedVersion})");
        Console.WriteLine($"Contract version: {VersionConstants.CliMcpContractVersion}");
        Console.WriteLine($"Status: {explanation}");
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
                contract_version = VersionConstants.CliMcpContractVersion,
                indexed = false,
                latest_failure = ToPayload(latestFailure)
            }, HandlerBootstrap.IndentedJson));
            return;
        }

        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Contract version: {VersionConstants.CliMcpContractVersion}");
        if (latestFailure != null)
            Console.WriteLine(LatestFailureLine(latestFailure));
        Console.WriteLine("Status: not indexed (no snapshot found). Run --mode=index to create one.");
    }

    private static void ReportSnapshotOnly(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, bool asJson, bool includeDocuments, bool includeReferences, bool includeCompleteness)
    {
        var latestSnapshotId = latestSnapshot.SnapshotId;
        var pin = ReadPinPreamble(store, latestSnapshot);
        if (asJson)
        {
            var timings = store.GetTimings(latestSnapshotId);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                schema_version = schemaVersion,
                contract_version = VersionConstants.CliMcpContractVersion,
                latest_snapshot_id = latestSnapshotId,
                effective_latest_snapshot_id = latestSnapshotId,
                built_at_latest_snapshot_id = pin.BuiltAtLatestSnapshotId,
                pinned_snapshot_id = pin.PinnedRow?.PinnedSnapshotId,
                pinned_at_utc = pin.PinnedRow?.PinnedAtUtc,
                previous_pinned_snapshot_id = pin.PinnedRow?.PreviousPinnedSnapshotId,
                stale_pinned_snapshot_id = pin.StalePinnedSnapshotId,
                pin_active = pin.PinActive,
                freshness_checked = false,
                note = "Pass --solution=path or set LURP_SOLUTION_PATH to check freshness against the current workspace.",
                timing_summary = timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms = timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : (long?)null,
                manifest = SnapshotManifestJson.Render(SnapshotManifestJson.WithBindingCompleteness(store, latestSnapshot, includeCompleteness), includeDocuments, includeReferences, "--detail=documents", "--detail=references"),
                latest_failure = ToPayload(pin.LatestFailure)
            }, HandlerBootstrap.IndentedJson));
            return;
        }

        WriteTextHeader(dbPath, schemaVersion, latestSnapshotId, pin);
        Console.WriteLine("Freshness: unknown : pass --solution=path or set LURP_SOLUTION_PATH to compare against the current workspace.");
        ShowTimingIfAvailable(store, latestSnapshotId);
    }

    private static void ReportFreshness(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, WorkspaceFreshness.FreshnessResult freshness, bool asJson, bool includeDocuments, bool includeReferences, bool includeCompleteness, int maxMismatches = 50)
    {
        var latestSnapshotId = latestSnapshot.SnapshotId;
        var pin = ReadPinPreamble(store, latestSnapshot);
        if (asJson)
        {
            var timings = store.GetTimings(latestSnapshotId);

            var capped = FreshnessJson.CapMismatches(freshness.Mismatches, maxMismatches);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                database_path = dbPath,
                schema_version = schemaVersion,
                contract_version = VersionConstants.CliMcpContractVersion,
                latest_snapshot_id = latestSnapshotId,
                effective_latest_snapshot_id = latestSnapshotId,
                built_at_latest_snapshot_id = pin.BuiltAtLatestSnapshotId,
                pinned_snapshot_id = pin.PinnedRow?.PinnedSnapshotId,
                pinned_at_utc = pin.PinnedRow?.PinnedAtUtc,
                previous_pinned_snapshot_id = pin.PinnedRow?.PreviousPinnedSnapshotId,
                stale_pinned_snapshot_id = pin.StalePinnedSnapshotId,
                pin_active = pin.PinActive,
                is_fresh = freshness.IsFresh,
                mismatches = capped.Rows,
                mismatches_truncated = capped.Truncated,
                mismatches_total = capped.Total,
                timing_summary = timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms = timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : (long?)null,
                manifest = SnapshotManifestJson.Render(SnapshotManifestJson.WithBindingCompleteness(store, latestSnapshot, includeCompleteness), includeDocuments, includeReferences, "--detail=documents", "--detail=references"),
                latest_failure = ToPayload(pin.LatestFailure)
            }, HandlerBootstrap.IndentedJson));
            return;
        }

        WriteTextHeader(dbPath, schemaVersion, latestSnapshotId, pin);
        Console.WriteLine(freshness.IsFresh ? "Freshness: up to date." : $"Freshness: stale ({freshness.Mismatches.Count} mismatch(es)).");

        foreach (var mismatch in freshness.Mismatches) Console.WriteLine($"  [{mismatch.Kind}] {mismatch.Description}");

        ShowTimingIfAvailable(store, latestSnapshotId);
    }

    /// <summary>The header lines both <c>status</c> text branches print: database, schema, contract, latest snapshot, pin block, stale pin and latest failure.</summary>
    private static void WriteTextHeader(string dbPath, int schemaVersion, string latestSnapshotId, PinPreamble pin)
    {
        Console.WriteLine($"Database: {dbPath}");
        Console.WriteLine($"Schema version: {schemaVersion}");
        Console.WriteLine($"Contract version: {VersionConstants.CliMcpContractVersion}");
        Console.WriteLine($"Latest snapshot: {latestSnapshotId}");
        if (pin.PinActive)
        {
            var pinnedRow = pin.PinnedRow!;
            Console.WriteLine($"Pinned snapshot: {pinnedRow.PinnedSnapshotId} (pinned at {pinnedRow.PinnedAtUtc:O}, built at {pinnedRow.BuiltAtUtc:O})");
            if (!string.Equals(latestSnapshotId, pin.BuiltAtLatestSnapshotId, StringComparison.Ordinal))
                Console.WriteLine($"  Note: effective latest is pinned; built-at latest is {pin.BuiltAtLatestSnapshotId}");
            if (pinnedRow.PreviousPinnedSnapshotId != null)
                Console.WriteLine($"  Previous pin: {pinnedRow.PreviousPinnedSnapshotId}");
        }
        if (pin.StalePinnedSnapshotId != null)
            Console.WriteLine(StalePinStatusLine(pin.StalePinnedSnapshotId));
        if (pin.LatestFailure != null)
            Console.WriteLine(LatestFailureLine(pin.LatestFailure));
    }

    /// <summary>The pin and failure facts both <c>status</c> branches print.</summary>
    private sealed record PinPreamble(PinnedSnapshotRow? PinnedRow, string? StalePinnedSnapshotId,
        string? BuiltAtLatestSnapshotId, bool PinActive, SnapshotFailureRow? LatestFailure);

    private static PinPreamble ReadPinPreamble(SqliteIndexStore store, SnapshotRow latestSnapshot)
    {
        var pinnedRow = store.GetPinnedSnapshot();
        return new PinPreamble(
            pinnedRow,
            store.GetStalePinnedSnapshotId(),
            store.GetBuiltAtLatestSnapshotId(),
            pinnedRow != null,
            store.GetLatestSnapshotFailure(latestSnapshot.WorkspaceId));
    }

    private static string StalePinStatusLine(string stalePinnedSnapshotId) =>
        $"Pin: snapshot '{stalePinnedSnapshotId}' is not complete and is ignored. Clear it with --mode=pin-snapshot --clear.";

    private static string LatestFailureLine(SnapshotFailureRow row) =>
        $"Latest failed index: snapshot '{row.SnapshotId}' at {row.CreatedAtUtc:O} ({row.ReasonCode})"
        + (row.Message == null ? "" : $": {row.Message}");

    private sealed record LatestFailurePayload(string snapshot_id, string reason_code, string? message, DateTime created_at_utc);

    private static LatestFailurePayload? ToPayload(SnapshotFailureRow? row) =>
        row == null ? null : new LatestFailurePayload(row.SnapshotId, row.ReasonCode, row.Message, row.CreatedAtUtc);

    private static void ShowTimingIfAvailable(SqliteIndexStore store, string snapshotId)
    {
        var timings = store.GetTimings(snapshotId);

        if (timings.Count == 0) return;

        var totalMs = timings.Sum(t => t.ElapsedMs);
        Console.WriteLine($"Timing summary ({totalMs} ms total):");
        foreach (var t in timings) Console.WriteLine($"  {t.StepName}: {t.ElapsedMs} ms");
    }

    private static int ParseMaxArg(string[] args, string prefix, int defaultValue)
    {
        var raw = HandlerBootstrap.GetArgValue(args, prefix);
        if (string.IsNullOrEmpty(raw))
            return defaultValue;
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
            HandlerBootstrap.Fail($"ERROR: {prefix.TrimEnd('=')} must be a positive integer.");
        return value;
    }
}