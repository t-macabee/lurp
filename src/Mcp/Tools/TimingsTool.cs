using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class TimingsTool
{
    private readonly McpSessionContext _session;

    public TimingsTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_timings", Title = "Lurp Timings", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(TimingsResponse))]
    [Description("Show step-by-step timing data and snapshot metrics for the pinned snapshot.")]
    public string LurpTimings(
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            var built = TimingsPayload.Build(store, snapshotId);
            var freshness = _session.GetFreshnessJson();

            var envelope = new TimingsResponse(snapshotId, freshness, pinned: true, built.TotalMs, built.Steps, built.Metrics);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}

internal sealed record TimingsResponse(
    string snapshot_id, object freshness, bool pinned, long total_ms,
    IReadOnlyList<TimingsPayload.StepEntry> steps, IReadOnlyDictionary<string, long> metrics);
