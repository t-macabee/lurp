using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class FindSymbolTool
{
    private readonly McpSessionContext _session;

    public FindSymbolTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_find_symbol", Title = "Lurp Find Symbol", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Resolve a symbol in the pinned snapshot. " + HandlerBootstrap.AcceptedSymbolFormsText + " More than one match is an error.")]
    public string LurpFindSymbol(
        [McpRequired] string? symbol = null,
        bool? include_generated = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;

            var info = HandlerBootstrap.ResolveSymbolInfo(store, symbol, snapshotId, includeGenerated);

            var locations = store.GetDeclarationLocations(info.SymbolId.Value, snapshotId, includeGenerated: true);
            var freshness = _session.GetFreshnessJson();

            var envelope = McpSymbolEnvelope.Build(snapshotId, freshness, info, locations);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}
