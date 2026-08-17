using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class RefreshTool
{
    private readonly McpSessionContext _session;

    public RefreshTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_refresh", Title = "Lurp Refresh", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Check for a newer snapshot and optionally advance the pin. Without ack, reports old/new without moving. With ack equal to latest, closes and reopens store and re-pins.")]
    public string LurpRefresh(
        string? ack = null,
        string? snapshot_id = null)
    {
        try
        {
            // Validate snapshot_id param against pin if provided (hardened pin validation)
            if (!string.IsNullOrEmpty(snapshot_id))
                _session.RequirePinnedSnapshot(snapshot_id);

            var oldSnapshotId = _session.PinnedSnapshotId;
            var latest = _session.GetLatestSnapshotId() ?? oldSnapshotId;
            var changed = !string.Equals(oldSnapshotId, latest, StringComparison.Ordinal);

            // No ack: report without moving
            if (string.IsNullOrEmpty(ack))
            {
                var freshness = _session.GetFreshnessJson();
                var requiresAck = changed;
                var envelope = new
                {
                    old_snapshot_id = oldSnapshotId,
                    new_snapshot_id = latest,
                    changed,
                    freshness,
                    requires_ack = requiresAck
                };
                return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            }

            // Ack provided: must equal latest
            if (!string.Equals(ack, latest, StringComparison.Ordinal))
                throw new McpProtocolException($"snapshot mismatch: session pinned to {oldSnapshotId}; call lurp_refresh to advance.", McpErrorCode.InvalidParams);

            // If ack equals old (no change), no advance needed
            if (string.Equals(ack, oldSnapshotId, StringComparison.Ordinal))
            {
                var freshness = _session.GetFreshnessJson();
                var envelopeSame = new
                {
                    old_snapshot_id = oldSnapshotId,
                    new_snapshot_id = latest,
                    changed = false,
                    freshness,
                    requires_ack = false,
                    pinned = true
                };
                return JsonSerializer.Serialize(envelopeSame, new JsonSerializerOptions { WriteIndented = true });
            }

            // Ack equals latest and differs from old: advance pin
            _session.AdvancePin(latest);
            var newFreshness = _session.GetFreshnessJson();
            var envelopeAdv = new
            {
                old_snapshot_id = oldSnapshotId,
                new_snapshot_id = latest,
                changed = false,
                freshness = newFreshness,
                pinned = true
            };
            return JsonSerializer.Serialize(envelopeAdv, new JsonSerializerOptions { WriteIndented = true });
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
}
