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

    [McpServerTool(Name = "lurp_refresh", Title = "Lurp Refresh", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(RefreshResponse))]
    [Description("Check for a newer snapshot and optionally advance the session pin. Without ack, changed and requires_ack indicate whether a newer snapshot is pending. To acknowledge, pass the new_snapshot_id from that response as ack. The server checks the database schema version on each call and keeps the pin in session memory. An accepted ack returns pinned=true, changed=false and requires_ack=false, even when the pin was already current. Compare old_snapshot_id and new_snapshot_id in the acknowledged response to see whether the pin moved.")]
    public string LurpRefresh(
        string? ack = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        McpSessionContext.RunTool(() =>
        {
            // Validate snapshot_id param against pin if provided (hardened pin validation)
            if (!string.IsNullOrEmpty(snapshot_id))
                _session.RequirePinnedSnapshot(snapshot_id);

            var oldSnapshotId = _session.PinnedSnapshotId;
            var latest = _session.GetLatestSnapshotId() ?? oldSnapshotId;
            var changed = !string.Equals(oldSnapshotId, latest, StringComparison.Ordinal);

            if (string.IsNullOrEmpty(ack))
            {
                var response = new RefreshResponse(oldSnapshotId, latest, changed, changed, false, _session.GetFreshnessJson());
                return JsonSerializer.Serialize(response, LurpJsonOptions.Indented);
            }

            if (!string.Equals(ack, latest, StringComparison.Ordinal))
                throw new McpProtocolException($"ack must equal the new_snapshot_id of a lurp_refresh call without ack ({latest}).", McpErrorCode.InvalidParams);

            if (changed)
                _session.AdvancePin(latest);

            var acknowledged = new RefreshResponse(oldSnapshotId, latest, false, false, true, _session.GetFreshnessJson());
            return JsonSerializer.Serialize(acknowledged, LurpJsonOptions.Indented);
        }, cancellationToken);
}

internal sealed record RefreshResponse(
    string old_snapshot_id, string new_snapshot_id, bool changed, bool requires_ack, bool pinned, object freshness);
