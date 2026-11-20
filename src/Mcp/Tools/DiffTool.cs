using System.ComponentModel;
using System.Text.Json;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class DiffTool
{
    private readonly McpSessionContext _session;

    public DiffTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_diff", Title = "Lurp Diff", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compute semantic diff between two snapshots. Both from_snapshot and to_snapshot are required.")]
    public string LurpDiff(
        [McpRequired] string? from_snapshot = null,
        [McpRequired] string? to_snapshot = null,
        CancellationToken cancellationToken = default) =>
        McpSessionContext.RunTool(() =>
        {
            using var store = _session.OpenReadStore();
            if (string.IsNullOrEmpty(from_snapshot))
                throw new McpProtocolException("from_snapshot is required.", McpErrorCode.InvalidParams);
            if (string.IsNullOrEmpty(to_snapshot))
                throw new McpProtocolException("to_snapshot is required.", McpErrorCode.InvalidParams);

            var pinnedSnapshotId = _session.PinnedSnapshotId;
            var fromSnapshotId = McpSessionContext.IsLatestAlias(from_snapshot) ? pinnedSnapshotId : from_snapshot;
            var toSnapshotId = McpSessionContext.IsLatestAlias(to_snapshot) ? pinnedSnapshotId : to_snapshot;

            ValidateSnapshotExists(store, fromSnapshotId);
            ValidateSnapshotExists(store, toSnapshotId);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, skippedComparisons) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var freshnessStamp = WorkspaceFreshness.CheckFreshnessCheap(store, toSnapshotId, FreshnessMode.Auto);
            var freshness = FreshnessJson.Cheap(freshnessStamp);
            var isPinned = string.Equals(toSnapshotId, pinnedSnapshotId, StringComparison.Ordinal);

            var envelope = new
            {
                snapshot_id = toSnapshotId,
                freshness,
                pinned = isPinned,
                from_snapshot = fromSnapshotId,
                to_snapshot = toSnapshotId,
                change_count = changes.Count,
                skipped_comparisons = skippedComparisons,
                changes = changes.Select(static c => new
                {
                    change_id = c.ChangeId,
                    change_type = c.ChangeType,
                    symbol_id = c.SymbolId,
                    detail = c.DetailJson != null ? JsonSerializer.Deserialize<object>(c.DetailJson) : null,
                    created_at_utc = c.CreatedAtUtc
                }).ToList()
            };

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);

    private static void ValidateSnapshotExists(SqliteIndexStore store, string snapshotId)
    {
        var meta = store.LoadSnapshotMetadata(snapshotId);
        if (meta == null)
            throw new McpProtocolException($"snapshot '{snapshotId}' not found.", McpErrorCode.InvalidParams);
    }
}
