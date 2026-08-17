using System.ComponentModel;
using System.Text.Json;
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

    [McpServerTool(Name = "lurp_timings", Title = "Lurp Timings", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Show step-by-step timing data for the pinned snapshot.")]
    public string LurpTimings(
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            var timings = _session.Store.GetTimings(snapshotId);
            var freshness = _session.GetFreshnessJson();

            var totalMs = timings.Sum(t => t.ElapsedMs);
            var steps = timings.Select(t => new
            {
                step = t.StepName,
                elapsed_ms = t.ElapsedMs,
                percent = totalMs > 0 ? Math.Round((double)t.ElapsedMs / totalMs * 100, 1) : 0
            }).ToList();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                total_ms = totalMs,
                steps
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
}
