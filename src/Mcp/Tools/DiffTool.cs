using System.ComponentModel;
using System.Text.Json;
using Lurp.Workspace;
using Lurp.Storage;
using Lurp.Handlers;
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

    [McpServerTool(Name = "lurp_diff", Title = "Lurp Diff", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(DiffResponse))]
    [Description("Compute a paged semantic diff between two snapshots. Both snapshots are required; limit defaults to 100. Pass next_cursor to continue.")]
    public string LurpDiff(
        [McpRequired] string? from_snapshot = null,
        [McpRequired] string? to_snapshot = null,
        int? limit = null,
        string? cursor = null,
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

            var pageSize = McpLimits.RequirePositiveAtMost(limit, 100, McpLimits.MaxLimit, "limit");
            var fingerprint = SequenceCursor.ComputeFingerprint(fromSnapshotId, toSnapshotId);
            if (!RequestError.TryDecodeAndValidateCursor(cursor, toSnapshotId, fingerprint, "diff", out var offset, out var error))
                throw new McpProtocolException(error!.FormatMcp(), McpErrorCode.InvalidParams);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, skippedComparisons) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var freshnessStamp = WorkspaceFreshness.CheckFreshnessCheap(store, toSnapshotId, FreshnessMode.Auto);
            var freshness = FreshnessJson.Cheap(freshnessStamp);
            var isPinned = string.Equals(toSnapshotId, pinnedSnapshotId, StringComparison.Ordinal);

            var rows = changes.Skip(offset).Take(pageSize).Select(static c => new DiffChange(c.ChangeId, c.ChangeType, c.SymbolId,
                c.DetailJson != null ? JsonSerializer.Deserialize<JsonElement>(c.DetailJson) : null,
                c.CreatedAtUtc)).ToList();
            var envelope = new DiffResponse(toSnapshotId, freshness, isPinned, fromSnapshotId, toSnapshotId,
                changes.Count, skippedComparisons, offset + rows.Count < changes.Count ? new SequenceCursor(toSnapshotId, fingerprint, "diff", offset + rows.Count).Encode() : null, rows);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);

    private static void ValidateSnapshotExists(SqliteIndexStore store, string snapshotId)
    {
        var meta = store.LoadSnapshotMetadata(snapshotId);
        if (meta == null)
            throw new McpProtocolException($"snapshot '{snapshotId}' not found.", McpErrorCode.InvalidParams);
    }
}

internal sealed record DiffResponse(
    string snapshot_id, object freshness, bool pinned, string from_snapshot, string to_snapshot,
    int change_count, int skipped_comparisons, string? next_cursor, IReadOnlyList<DiffChange> changes);

internal sealed record DiffChange(
    string change_id, string change_type, string symbol_id, JsonElement? detail, DateTime created_at_utc);
