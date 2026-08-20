using Lurp.Workspace;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class PinSnapshotHandler
{
    public static void Run(string[] args)
    {
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);
        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg), "index.db");
        if (!File.Exists(dbPath))
            HandlerBootstrap.Fail("ERROR: Index database not found at " + dbPath);

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var clear = args.Contains("--clear");
        var asJson = args.Contains("--json") || string.Equals(HandlerBootstrap.GetArgValue(args, "--output="), "json", StringComparison.OrdinalIgnoreCase);

        var store = HandlerBootstrap.OpenStore(dbPath);
        try
        {
            store.RunMigrations();
            store.ValidateSchema(VersionConstants.DatabaseSchemaVersion);

            // Clear path: --clear or --snapshot=latest
            var isLatestLiteral = string.Equals(snapshotArg, "latest", StringComparison.OrdinalIgnoreCase);
            if (clear || isLatestLiteral)
            {
                var cleared = store.ClearPinnedSnapshot();
                var effectiveLatest = store.GetLatestSnapshotId();
                var builtAtLatest = store.GetBuiltAtLatestSnapshotId();
                if (asJson)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        status = "cleared",
                        cleared,
                        effective_latest_snapshot_id = effectiveLatest,
                        built_at_latest_snapshot_id = builtAtLatest,
                        pin_active = false
                    }, HandlerBootstrap.IndentedJson));
                }
                else
                {
                    if (cleared)
                        Console.WriteLine($"Pin cleared. Effective latest is now built-at latest: {effectiveLatest ?? "(none)"}");
                    else
                        Console.WriteLine($"No pin was set. Effective latest remains: {effectiveLatest ?? "(none)"}");
                    if (!string.Equals(effectiveLatest, builtAtLatest, StringComparison.Ordinal))
                        Console.WriteLine($"Note: effective latest ({effectiveLatest}) still differs from built-at latest ({builtAtLatest}) — pin table may still hold a row for another workspace.");
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

            var previousPin = store.GetPinnedSnapshot();
            var previousId = previousPin?.PinnedSnapshotId;

            try
            {
                store.SetPinnedSnapshot(snapshotId);
            }
            catch (InvalidOperationException ex)
            {
                HandlerBootstrap.Fail(ex.Message);
                return;
            }

            var pinnedRow = store.GetPinnedSnapshot();
            var builtAtLatestId = store.GetBuiltAtLatestSnapshotId();
            var effectiveLatestId = store.GetLatestSnapshotId();

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    status = "pinned",
                    pinned_snapshot_id = pinnedRow?.PinnedSnapshotId ?? snapshotId,
                    previous_pinned_snapshot_id = previousId,
                    pinned_at_utc = pinnedRow?.PinnedAtUtc,
                    built_at_utc = pinnedRow?.BuiltAtUtc,
                    effective_latest_snapshot_id = effectiveLatestId,
                    built_at_latest_snapshot_id = builtAtLatestId,
                    pin_active = true
                }, HandlerBootstrap.IndentedJson));
            }
            else
            {
                Console.WriteLine($"Pinned snapshot {snapshotId} (workspace {workspaceId}) at {pinnedRow?.PinnedAtUtc:O}");
                if (previousId != null && !string.Equals(previousId, snapshotId, StringComparison.Ordinal))
                    Console.WriteLine($"  Previous pin: {previousId}");
                Console.WriteLine($"  Effective latest (reads default to): {effectiveLatestId}");
                Console.WriteLine($"  Built-at latest (most recent build): {builtAtLatestId}");
                if (!string.Equals(effectiveLatestId, builtAtLatestId, StringComparison.Ordinal))
                    Console.WriteLine($"  Note: reads with no --snapshot= will now resolve to the pinned snapshot, not the most recently built one.");
            }
        }
        finally
        {
            store.Close();
        }
    }
}
