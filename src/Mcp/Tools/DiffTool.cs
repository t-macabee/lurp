using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
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
        string? from_snapshot = null,
        string? to_snapshot = null)
    {
        try
        {
            if (string.IsNullOrEmpty(from_snapshot))
                throw new McpProtocolException("from-snapshot is required.", McpErrorCode.InvalidParams);
            if (string.IsNullOrEmpty(to_snapshot))
                throw new McpProtocolException("to-snapshot is required.", McpErrorCode.InvalidParams);

            ValidateSnapshotExists(from_snapshot);
            ValidateSnapshotExists(to_snapshot);

            var differ = new SemanticDiffer(_session.Store, _session.Store, _session.Store, _session.Store);
            var (changes, skippedComparisons) = differ.ComputeDiff(from_snapshot, to_snapshot);

            var payload = new
            {
                from_snapshot = from_snapshot,
                to_snapshot = to_snapshot,
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

            var freshnessStamp = WorkspaceFreshness.CheckFreshnessCheap(_session.Store, _session.Store, to_snapshot, FreshnessMode.Auto);
            var freshness = HandlerBootstrap.FreshnessJson(freshnessStamp);
            var isPinned = string.Equals(to_snapshot, _session.PinnedSnapshotId, StringComparison.Ordinal);

            var envelope = new
            {
                snapshot_id = to_snapshot,
                freshness,
                pinned = isPinned,
                from_snapshot = payload.from_snapshot,
                to_snapshot = payload.to_snapshot,
                change_count = payload.change_count,
                skipped_comparisons = payload.skipped_comparisons,
                changes = payload.changes
            };

            return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    private void ValidateSnapshotExists(string snapshotId)
    {
        var meta = _session.Store.LoadSnapshotMetadata(snapshotId);
        if (meta == null)
            throw new McpProtocolException($"snapshot '{snapshotId}' not found.", McpErrorCode.InvalidParams);
    }
}
