using Lurp.Workspace;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class PinSnapshotHandler
{
    public static void Run(string[] args, IOutputSink output)
    {
        // Parsed before the database check, so a bad --output= fails the same way whatever the database state (B70).
        var asJson = args.Contains("--json") || HandlerBootstrap.ParseOutputMode(args, allowJsonl: false, defaultMode: OutputMode.Summary) == OutputMode.Json;

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args, output);
        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
        if (!File.Exists(dbPath))
            HandlerBootstrap.Fail("ERROR: Index database not found at " + dbPath);

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var clear = args.Contains("--clear");

        var store = HandlerBootstrap.OpenStoreUnchecked(dbPath);
        try
        {
            try
            {
                store.RunMigrations();
            }
            catch (NewerSchemaException ex)
            {
                HandlerBootstrap.Fail("ERROR: " + ex.Message);
            }

            // Clear path: --clear or --snapshot=latest
            var isLatestLiteral = string.Equals(snapshotArg, "latest", StringComparison.OrdinalIgnoreCase);
            if (clear && !string.IsNullOrEmpty(snapshotArg) && !isLatestLiteral)
                HandlerBootstrap.Fail("ERROR: --clear cannot be combined with --snapshot=<id>. Pass one of them.");
            if (clear || isLatestLiteral)
            {
                var cleared = store.ClearPinnedSnapshot();
                var effectiveLatest = store.GetLatestSnapshotId();
                var selectedLatest = store.GetSelectedLatestSnapshotId();
                if (asJson)
                {
                    output.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "cleared",
                        cleared,
                        effective_latest_snapshot_id = effectiveLatest,
                        selected_latest_snapshot_id = selectedLatest,
                        pin_active = false
                    }, HandlerBootstrap.IndentedJson));
                }
                else
                {
                    if (cleared)
                        output.WriteLine($"Pin cleared. Effective latest is now the selected latest: {effectiveLatest ?? "(none)"}");
                    else
                        output.WriteLine($"No pin was set. Effective latest remains: {effectiveLatest ?? "(none)"}");
                }
                return;
            }

            if (string.IsNullOrEmpty(snapshotArg))
                HandlerBootstrap.Fail("ERROR: --snapshot=<id> is required for --mode=pin-snapshot (or pass --clear / --snapshot=latest to clear the pin).");

            var snapshotId = snapshotArg!;

            // Validate snapshot exists and is complete
            var meta = store.LoadSnapshot(snapshotId);
            if (meta == null)
                HandlerBootstrap.Fail($"ERROR: snapshot '{snapshotId}' not found.");

            var workspaceId = meta!.WorkspaceId;
            var status = store.GetSnapshotStatus(snapshotId, workspaceId);
            if (!string.Equals(status, SnapshotStatusValues.Complete, StringComparison.Ordinal))
                HandlerBootstrap.Fail($"ERROR: snapshot '{snapshotId}' has status '{status}' and cannot be pinned. Only snapshots with status 'complete' can be pinned.");

            HandlerBootstrap.ClearStalePinWithNotice(store, output);
            var previousPin = store.GetPinnedSnapshot(workspaceId);
            var previousId = previousPin?.PinnedSnapshotId;

            try
            {
                store.SetPinnedSnapshot(snapshotId);
            }
            catch (InvalidOperationException ex)
            {
                HandlerBootstrap.Fail("ERROR: " + ex.Message);
                return;
            }

            var pinnedRow = store.GetPinnedSnapshot(workspaceId);
            if (pinnedRow == null)
                HandlerBootstrap.Fail($"ERROR: the pin on snapshot '{snapshotId}' was not stored.");
            var selectedLatestId = store.GetSelectedLatestSnapshotId();
            var effectiveLatestId = store.GetLatestSnapshotId();

            if (asJson)
            {
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "pinned",
                    pinned_snapshot_id = pinnedRow.PinnedSnapshotId,
                    previous_pinned_snapshot_id = previousId,
                    pinned_at_utc = pinnedRow.PinnedAtUtc,
                    built_at_utc = pinnedRow.BuiltAtUtc,
                    effective_latest_snapshot_id = effectiveLatestId,
                    selected_latest_snapshot_id = selectedLatestId,
                    pin_active = true
                }, HandlerBootstrap.IndentedJson));
            }
            else
            {
                output.WriteLine($"Pinned snapshot {snapshotId} (workspace {workspaceId}) at {pinnedRow.PinnedAtUtc:O}");
                if (previousId != null && !string.Equals(previousId, snapshotId, StringComparison.Ordinal))
                    output.WriteLine($"  Previous pin: {previousId}");
                output.WriteLine($"  Effective latest (reads default to): {effectiveLatestId}");
                output.WriteLine($"  Selected latest (last index run): {selectedLatestId}");
                if (!string.Equals(effectiveLatestId, selectedLatestId, StringComparison.Ordinal))
                    output.WriteLine($"  Note: reads with no --snapshot= will now resolve to the pinned snapshot, not the snapshot that the last index run selected.");
            }
        }
        finally
        {
            store.Close();
        }
    }
}
