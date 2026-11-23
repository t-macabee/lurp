using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class ImpactTool
{
    private readonly McpSessionContext _session;

    public ImpactTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_impact", Title = "Lurp Impact", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(ImpactResponse))]
    [Description("Trace impact downstream or upstream for a symbol: the reached symbols with depth, shortest-path count, frontier flag and a deterministic witness path, plus first-hop groups. Supports kinds/provenance filtering and cursor pagination. The upstream direction follows every edge kind except Declares, Contains and TestedBy unless kinds names the set; the downstream direction follows every kind.")]
    public string LurpImpact(
        [McpRequired] string? symbol = null,
        string? direction = null,
        string[]? kinds = null,
        string[]? provenance = null,
        int? max_depth = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            var (request, parseError) = ImpactRequest.Parse(
                symbol,
                direction,
                kinds,
                provenance,
                max_depth?.ToString(CultureInfo.InvariantCulture),
                limit?.ToString(CultureInfo.InvariantCulture),
                cursor);
            if (parseError != null)
                throw new McpProtocolException(parseError.FormatMcp(), McpErrorCode.InvalidParams);

            McpLimits.RequireAtMost(max_depth, McpLimits.MaxMaxDepth, "max_depth");
            McpLimits.RequireAtMost(limit, McpLimits.MaxLimit, "limit");

            var (binding, bindError) = request!.Bind(store, snapshotId);
            if (bindError != null)
                throw new McpProtocolException(bindError.FormatMcp(), McpErrorCode.InvalidParams);

            var reachability = new ImpactReachability(store, snapshotId);
            var traced = reachability.Trace(binding!.ResolvedSymbolId, request.Direction, request.MaxDepth, request.AllowedKinds, request.AllowedProvenance, includeSource: true, cancellationToken: cancellationToken);
            var semanticCauses = store.GetSemanticChangesForSymbol(snapshotId, binding.ResolvedSymbolId);

            var response = ImpactPaging.BuildSymbolsResponse(
                snapshotId,
                _session.GetFreshnessJson(),
                binding.ResolvedSymbolId,
                request.Direction,
                request.MaxDepth,
                traced,
                reachability,
                semanticCauses,
                binding.Offset,
                request.Limit,
                binding.Fingerprint,
                ImpactRequest.CursorKind,
                pinned: true,
                includeLimitEcho: true);

            return JsonSerializer.Serialize(response, LurpJsonOptions.Indented);
        }, cancellationToken);
}
