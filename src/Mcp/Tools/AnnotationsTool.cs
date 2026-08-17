using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class AnnotationsTool
{
    private readonly McpSessionContext _session;

    public AnnotationsTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_get_annotations", Title = "Lurp Get Annotations", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get annotations for a symbol in the pinned snapshot. Annotations are stored separately from compiler facts.")]
    public string LurpGetAnnotations(
        string? symbol = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("--symbol is required.", McpErrorCode.InvalidParams);

            var resolved = HandlerBootstrap.ResolveSymbolArg(_session.Store, symbol, snapshotId);

            var annotations = _session.Store.GetAnnotations(snapshotId, resolved);
            var freshness = _session.GetFreshnessJson();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                symbol_id = resolved,
                annotations = annotations.Select(static a => new
                {
                    symbol_id = a.SymbolId,
                    kind = a.Kind,
                    value = a.Value,
                    document_path = a.DocumentPath
                }).ToList()
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
