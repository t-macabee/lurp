using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class GetSymbolTool
{
    private readonly McpSessionContext _session;

    public GetSymbolTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_get_symbol", Title = "Lurp Get Symbol", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get symbol metadata and optional source. " + HandlerBootstrap.AcceptedSymbolFormsText + " More than one match is an error. View summary|source|all controls payload.")]
    public string LurpGetSymbol(
        [McpRequired] string? symbol = null,
        string? view = null,
        int? context_lines = null,
        bool? include_generated = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);

            var viewArg = string.IsNullOrEmpty(view) ? "summary" : view.ToLowerInvariant();
            if (viewArg != "summary" && viewArg != "source" && viewArg != "all")
                throw new McpProtocolException("view must be one of: summary, source, all.", McpErrorCode.InvalidParams);

            if (context_lines.HasValue && context_lines.Value < 0)
                throw new McpProtocolException("context_lines must be a non-negative integer.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;
            var contextLines = context_lines ?? GetSymbolHandler.DefaultContextLines;

            var info = HandlerBootstrap.ResolveSymbolInfo(store, symbol, snapshotId, includeGenerated);

            var freshness = _session.GetFreshnessJson();
            var locations = store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated: true);
            var annotations = store.GetAnnotations(snapshotId, info.SymbolId.Value);

            string? source = null;
            if (viewArg is "source" or "all")
            {
                if (context_lines.HasValue)
                {
                    source = store.GetSurroundingLines(info.SymbolId.Value, snapshotId, contextLines, includeGenerated);
                    if (source == null)
                        source = store.GetSymbolSource(info.SymbolId.Value, snapshotId, ViewKind.Declaration, includeGenerated);
                }
                else
                {
                    source = store.GetSymbolSource(info.SymbolId.Value, snapshotId, ViewKind.Declaration, includeGenerated);
                }
            }

            var envelope = McpSymbolEnvelope.Build(snapshotId, freshness, info, locations);
            envelope["source"] = source;
            envelope["annotations"] = JsonSerializer.SerializeToNode(annotations.Select(SurfacePayloads.AnnotationRow).ToList(), LurpJsonOptions.Indented);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}
