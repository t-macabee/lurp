using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class GetSourceTool
{
    private readonly McpSessionContext _session;

    public GetSourceTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_get_source", Title = "Lurp Get Source", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Fetch source text for a document in the pinned snapshot.")]
    public string LurpGetSource(
        string? document = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            var normalized = HandlerBootstrap.NormalizeDocumentPath(document);
            if (string.IsNullOrEmpty(normalized))
                throw new McpProtocolException("--document is required.", McpErrorCode.InvalidParams);

            var source = _session.Store.GetSource(normalized, snapshotId);
            if (source == null)
                throw new McpProtocolException($"Document '{normalized}' not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                source,
                document = normalized
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
