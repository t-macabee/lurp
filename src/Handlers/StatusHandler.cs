using Lurp.Workspace;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class StatusHandler
{
    internal const int DefaultMaxMismatches = 50;

    public static async Task Run(string[] args, IOutputSink output)
    {
        // Parse every flag first before resolving the database directory (D72, D188.5).
        var asJson = HandlerBootstrap.ParseOutputMode(
                args,
                allowJsonl: false,
                defaultMode: args.Contains("--json") ? OutputMode.Json : OutputMode.Summary)
            == OutputMode.Json;

        var detailSections = default(StatusSectionSet);
        var detailArg = HandlerBootstrap.GetArgValue(args, "--detail=");
        if (!string.IsNullOrWhiteSpace(detailArg)
            && !StatusSections.TryParse(detailArg, allowSurfaceNames: false, out detailSections, out var badDetail))
            HandlerBootstrap.Fail($"ERROR: --detail: unknown value '{badDetail}'. Use {StatusSections.Vocabulary(false)}.");

        var maxMismatches = HandlerBootstrap.ParsePositiveIntArg(args, "--max-mismatches=", DefaultMaxMismatches);
        var explicitSolutionArg = HandlerBootstrap.GetArgValue(args, "--solution=");
        if (explicitSolutionArg != null && !File.Exists(explicitSolutionArg))
            HandlerBootstrap.Fail($"ERROR: --solution=<path> must point to an existing file: '{explicitSolutionArg}'.");

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args, output);
        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");

        if (!File.Exists(dbPath))
        {
            ReportNeverIndexed(dbPath, asJson, output);
            return;
        }

        var store = HandlerBootstrap.OpenReadStoreUnchecked(dbPath);

        try
        {
            var schemaVersion = store.GetCurrentSchemaVersion();
            if (schemaVersion != MigrationRunner.LatestVersion)
            {
                ReportSchemaMismatch(dbPath, schemaVersion, asJson, output);
                return;
            }

            var latestSnapshot = store.LoadLatestSnapshot();
            var latestSnapshotId = latestSnapshot?.SnapshotId;
            if (latestSnapshot == null || latestSnapshotId == null)
            {
                ReportNeverIndexed(dbPath, asJson, output, schemaVersion, store.GetLatestSnapshotFailure());
                return;
            }

            var includeDocuments = detailSections.Documents;
            var includeReferences = detailSections.References;
            var includeCompleteness = detailSections.Completeness;
            var solutionPathArg = explicitSolutionArg ?? Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH");
            if (string.IsNullOrEmpty(solutionPathArg) || !File.Exists(solutionPathArg))
            {
                ReportSnapshotOnly(store, dbPath, schemaVersion, latestSnapshot, asJson, includeDocuments, includeReferences, includeCompleteness, output);
                return;
            }

            var freshness = await CheckCurrentWorkspaceAsync(store, solutionPathArg!);
            ReportFreshness(store, dbPath, schemaVersion, latestSnapshot, freshness, asJson, includeDocuments, includeReferences, includeCompleteness, output, maxMismatches);
        }
        finally
        {
            store.Close();
        }
    }

    private static async Task<WorkspaceFreshness.FreshnessResult> CheckCurrentWorkspaceAsync(SqliteIndexStore store, string solutionPath)
    {
        var gitRoot = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        using var loader = new WorkspaceLoader(NullOutputSink.Instance);
        var loaded = await loader.LoadAsync(solutionPath, gitRoot, CancellationToken.None);
        var workspaceInfo = new WorkspaceInfo(loaded.Solution, gitRoot, NullOutputSink.Instance, loaded.UnloadedProjects);

        return WorkspaceFreshness.CheckFreshness(workspaceInfo, store);
    }

    private static void ReportSchemaMismatch(string dbPath, int actualVersion, bool asJson, IOutputSink output)
    {
        var expectedVersion = MigrationRunner.LatestVersion;
        var explanation = actualVersion < expectedVersion
            ? SchemaVersionMessages.Older(dbPath, actualVersion, expectedVersion)
            : SchemaVersionMessages.Newer(dbPath, actualVersion, expectedVersion);

        if (asJson)
        {
            WriteJson(new StatusJsonPayload(
                database_path: dbPath,
                database_exists: File.Exists(dbPath),
                schema_version: actualVersion,
                schema_version_expected: expectedVersion,
                schema_version_mismatch: true,
                schema_version_note: explanation,
                contract_version: VersionConstants.CliMcpContractVersion,
                indexed: null,
                latest_snapshot_id: null,
                effective_latest_snapshot_id: null,
                selected_latest_snapshot_id: null,
                pinned_snapshot_id: null,
                pinned_at_utc: null,
                previous_pinned_snapshot_id: null,
                stale_pinned_snapshot_id: null,
                pin_active: null,
                freshness_checked: false,
                note: null,
                is_fresh: null,
                mismatches: null,
                mismatches_truncated: null,
                mismatches_total: null,
                timing_summary: null,
                timing_total_ms: null,
                manifest: null,
                latest_failure: null), output);
            return;
        }

        output.WriteLine($"Database: {dbPath}");
        output.WriteLine($"Schema version: {actualVersion} (expected {expectedVersion})");
        output.WriteLine($"Contract version: {VersionConstants.CliMcpContractVersion}");
        output.WriteLine($"Status: {explanation}");
    }

    private static void ReportNeverIndexed(string dbPath, bool asJson, IOutputSink output, int? schemaVersion = null, SnapshotFailureRow? latestFailure = null)
    {
        if (asJson)
        {
            var exists = File.Exists(dbPath);
            WriteJson(new StatusJsonPayload(
                database_path: dbPath,
                database_exists: exists,
                schema_version: schemaVersion,
                schema_version_expected: MigrationRunner.LatestVersion,
                schema_version_mismatch: exists ? false : null,
                schema_version_note: null,
                contract_version: VersionConstants.CliMcpContractVersion,
                indexed: false,
                latest_snapshot_id: null,
                effective_latest_snapshot_id: null,
                selected_latest_snapshot_id: null,
                pinned_snapshot_id: null,
                pinned_at_utc: null,
                previous_pinned_snapshot_id: null,
                stale_pinned_snapshot_id: null,
                pin_active: null,
                freshness_checked: false,
                note: null,
                is_fresh: null,
                mismatches: null,
                mismatches_truncated: null,
                mismatches_total: null,
                timing_summary: null,
                timing_total_ms: null,
                manifest: null,
                latest_failure: ToPayload(latestFailure)), output);
            return;
        }

        output.WriteLine($"Database: {dbPath}");
        output.WriteLine($"Contract version: {VersionConstants.CliMcpContractVersion}");
        if (latestFailure != null)
            output.WriteLine(LatestFailureLine(latestFailure));
        output.WriteLine("Status: not indexed (no snapshot found). Run --mode=index to create one.");
    }

    private const string SnapshotOnlyNote = "Pass --solution=path or set LURP_SOLUTION_PATH to check freshness against the current workspace.";

    private static void ReportSnapshotOnly(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, bool asJson, bool includeDocuments, bool includeReferences, bool includeCompleteness, IOutputSink output)
    {
        var latestSnapshotId = latestSnapshot.SnapshotId;
        var pin = ReadPinPreamble(store, latestSnapshot);
        if (asJson)
        {
            var timings = store.GetTimings(latestSnapshotId);

            WriteJson(new StatusJsonPayload(
                database_path: dbPath,
                database_exists: true,
                schema_version: schemaVersion,
                schema_version_expected: MigrationRunner.LatestVersion,
                schema_version_mismatch: false,
                schema_version_note: null,
                contract_version: VersionConstants.CliMcpContractVersion,
                indexed: true,
                latest_snapshot_id: latestSnapshotId,
                effective_latest_snapshot_id: latestSnapshotId,
                selected_latest_snapshot_id: pin.SelectedLatestSnapshotId,
                pinned_snapshot_id: pin.PinnedRow?.PinnedSnapshotId,
                pinned_at_utc: pin.PinnedRow?.PinnedAtUtc,
                previous_pinned_snapshot_id: pin.PinnedRow?.PreviousPinnedSnapshotId,
                stale_pinned_snapshot_id: pin.StalePinnedSnapshotId,
                pin_active: pin.PinActive,
                freshness_checked: false,
                note: SnapshotOnlyNote,
                is_fresh: null,
                mismatches: null,
                mismatches_truncated: null,
                mismatches_total: null,
                timing_summary: timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms: timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : null,
                manifest: SnapshotManifestJson.Render(SnapshotManifestJson.WithBindingCompleteness(store, latestSnapshot, includeCompleteness), includeDocuments, includeReferences, "--detail=documents", "--detail=references"),
                latest_failure: ToPayload(pin.LatestFailure)), output);
            return;
        }

        WriteTextHeader(dbPath, schemaVersion, latestSnapshotId, latestSnapshot.AnalyzersSkipped, pin, output);
        output.WriteLine("Freshness: unknown : pass --solution=path or set LURP_SOLUTION_PATH to compare against the current workspace.");
        ShowTimingIfAvailable(store, latestSnapshotId, output);
    }

    private static void ReportFreshness(SqliteIndexStore store, string dbPath, int schemaVersion, SnapshotRow latestSnapshot, WorkspaceFreshness.FreshnessResult freshness, bool asJson, bool includeDocuments, bool includeReferences, bool includeCompleteness, IOutputSink output, int maxMismatches = 50)
    {
        var latestSnapshotId = latestSnapshot.SnapshotId;
        var pin = ReadPinPreamble(store, latestSnapshot);
        if (asJson)
        {
            var timings = store.GetTimings(latestSnapshotId);

            var capped = FreshnessJson.CapMismatches(freshness.Mismatches, maxMismatches);

            WriteJson(new StatusJsonPayload(
                database_path: dbPath,
                database_exists: true,
                schema_version: schemaVersion,
                schema_version_expected: MigrationRunner.LatestVersion,
                schema_version_mismatch: false,
                schema_version_note: null,
                contract_version: VersionConstants.CliMcpContractVersion,
                indexed: true,
                latest_snapshot_id: latestSnapshotId,
                effective_latest_snapshot_id: latestSnapshotId,
                selected_latest_snapshot_id: pin.SelectedLatestSnapshotId,
                pinned_snapshot_id: pin.PinnedRow?.PinnedSnapshotId,
                pinned_at_utc: pin.PinnedRow?.PinnedAtUtc,
                previous_pinned_snapshot_id: pin.PinnedRow?.PreviousPinnedSnapshotId,
                stale_pinned_snapshot_id: pin.StalePinnedSnapshotId,
                pin_active: pin.PinActive,
                freshness_checked: true,
                note: null,
                is_fresh: freshness.IsFresh,
                mismatches: capped.Rows,
                mismatches_truncated: capped.Truncated,
                mismatches_total: capped.Total,
                timing_summary: timings is { Count: > 0 } ? timings.Select(t => new { step = t.StepName, elapsed_ms = t.ElapsedMs }) : null,
                timing_total_ms: timings is { Count: > 0 } ? timings.Sum(t => t.ElapsedMs) : null,
                manifest: SnapshotManifestJson.Render(SnapshotManifestJson.WithBindingCompleteness(store, latestSnapshot, includeCompleteness), includeDocuments, includeReferences, "--detail=documents", "--detail=references"),
                latest_failure: ToPayload(pin.LatestFailure)), output);
            return;
        }

        WriteTextHeader(dbPath, schemaVersion, latestSnapshotId, latestSnapshot.AnalyzersSkipped, pin, output);
        output.WriteLine(freshness.IsFresh ? "Freshness: up to date." : $"Freshness: stale ({freshness.Mismatches.Count} mismatch(es)).");

        foreach (var mismatch in freshness.Mismatches) output.WriteLine($"  [{mismatch.Kind}] {mismatch.Description}");

        ShowTimingIfAvailable(store, latestSnapshotId, output);
    }

    private static void WriteJson(StatusJsonPayload payload, IOutputSink output)
    {
        output.WriteLine(JsonSerializer.Serialize(payload, HandlerBootstrap.IndentedJson));
    }

    private sealed record StatusJsonPayload(
        string database_path,
        bool database_exists,
        int? schema_version,
        int schema_version_expected,
        bool? schema_version_mismatch,
        string? schema_version_note,
        int contract_version,
        bool? indexed,
        string? latest_snapshot_id,
        string? effective_latest_snapshot_id,
        string? selected_latest_snapshot_id,
        string? pinned_snapshot_id,
        DateTime? pinned_at_utc,
        string? previous_pinned_snapshot_id,
        string? stale_pinned_snapshot_id,
        bool? pin_active,
        bool freshness_checked,
        string? note,
        bool? is_fresh,
        IReadOnlyList<object>? mismatches,
        bool? mismatches_truncated,
        int? mismatches_total,
        object? timing_summary,
        long? timing_total_ms,
        object? manifest,
        LatestFailurePayload? latest_failure);

    /// <summary>The header lines both <c>status</c> text branches print: database, schema, contract, latest snapshot, pin block, stale pin and latest failure.</summary>
    private static void WriteTextHeader(string dbPath, int schemaVersion, string latestSnapshotId, bool analyzersSkipped, PinPreamble pin, IOutputSink output)
    {
        output.WriteLine($"Database: {dbPath}");
        output.WriteLine($"Schema version: {schemaVersion}");
        output.WriteLine($"Contract version: {VersionConstants.CliMcpContractVersion}");
        output.WriteLine($"Latest snapshot: {latestSnapshotId}");
        if (analyzersSkipped)
            output.WriteLine("Analyzers: skipped (--skip-analyzers)");
        if (pin.PinActive)
        {
            var pinnedRow = pin.PinnedRow!;
            output.WriteLine($"Pinned snapshot: {pinnedRow.PinnedSnapshotId} (pinned at {pinnedRow.PinnedAtUtc:O}, built at {pinnedRow.BuiltAtUtc:O})");
            if (!string.Equals(latestSnapshotId, pin.SelectedLatestSnapshotId, StringComparison.Ordinal))
                output.WriteLine($"  Note: effective latest is pinned; selected latest is {pin.SelectedLatestSnapshotId}");
            if (pinnedRow.PreviousPinnedSnapshotId != null)
                output.WriteLine($"  Previous pin: {pinnedRow.PreviousPinnedSnapshotId}");
        }
        if (pin.StalePinnedSnapshotId != null)
            output.WriteLine(StalePinStatusLine(pin.StalePinnedSnapshotId));
        if (pin.LatestFailure != null)
            output.WriteLine(LatestFailureLine(pin.LatestFailure));
    }

    /// <summary>The pin and failure facts both <c>status</c> branches print.</summary>
    private sealed record PinPreamble(PinnedSnapshotRow? PinnedRow, string? StalePinnedSnapshotId,
        string? SelectedLatestSnapshotId, bool PinActive, SnapshotFailureRow? LatestFailure);

    private static PinPreamble ReadPinPreamble(SqliteIndexStore store, SnapshotRow latestSnapshot)
    {
        var pinnedRow = store.GetPinnedSnapshot();
        return new PinPreamble(
            pinnedRow,
            store.GetStalePinnedSnapshotId(),
            store.GetSelectedLatestSnapshotId(),
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

    private static void ShowTimingIfAvailable(SqliteIndexStore store, string snapshotId, IOutputSink output)
    {
        var timings = store.GetTimings(snapshotId);

        if (timings.Count == 0) return;

        var totalMs = timings.Sum(t => t.ElapsedMs);
        output.WriteLine($"Timing summary ({totalMs} ms total):");
        foreach (var t in timings) output.WriteLine($"  {t.StepName}: {t.ElapsedMs} ms");
    }
}