using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Queries;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class NavigateTool
{
    private readonly McpSessionContext _session;

    public NavigateTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_navigate", Title = "Lurp Navigate", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Navigate to the declaration containing a file+line in the pinned snapshot. Null target is success, not an error.")]
    public string LurpNavigate(
        string? file = null,
        int? line = null,
        bool? include_generated = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            var normalized = HandlerBootstrap.NormalizeDocumentPath(file);
            if (string.IsNullOrEmpty(normalized) || !line.HasValue)
                throw new McpProtocolException("file and line are required.", McpErrorCode.InvalidParams);

            if (line.Value < 1)
                throw new McpProtocolException("line must be a positive integer.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;

            var queries = new FastTravelQueries(_session.Store);
            var target = queries.Navigate(normalized, line.Value, snapshotId, includeGenerated);

            var freshness = _session.GetFreshnessJson();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                target
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
