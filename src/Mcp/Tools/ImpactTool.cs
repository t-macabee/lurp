using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class ImpactTool
{
    private const string CursorKind = "impact";
    private const int DefaultMaxDepth = 3;
    private const int DefaultLimit = 50;

    private readonly McpSessionContext _session;

    public ImpactTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_impact", Title = "Lurp Impact", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Trace impact downstream or upstream for a symbol: the reached symbols with depth, shortest-path count, frontier flag and a deterministic witness path, plus first-hop groups. Supports kinds/provenance filtering and cursor pagination.")]
    public string LurpImpact(
        string? symbol = null,
        string? direction = null,
        string[]? kinds = null,
        string[]? provenance = null,
        int? max_depth = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);
            cancellationToken.ThrowIfCancellationRequested();
            using var store = _session.OpenReadStore();

            if (string.IsNullOrEmpty(symbol))
                throw new McpProtocolException("symbol is required.", McpErrorCode.InvalidParams);

            var directionArg = string.IsNullOrEmpty(direction) ? "downstream" : direction;
            var impactDirection = directionArg.ToLowerInvariant() switch
            {
                "downstream" => ImpactDirection.Downstream,
                "upstream" => ImpactDirection.Upstream,
                _ => throw new McpProtocolException("direction must be one of: downstream, upstream.", McpErrorCode.InvalidParams)
            };

            var maxDepth = max_depth ?? DefaultMaxDepth;
            if (maxDepth < 1)
                throw new McpProtocolException("max-depth must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(max_depth, McpLimits.MaxMaxDepth, "max_depth");

            var limitValue = limit ?? DefaultLimit;
            if (limitValue < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);
            McpLimits.RequireAtMost(limit, McpLimits.MaxLimit, "limit");

            HashSet<string>? allowedKinds = null;
            string? kindsRaw = null;
            if (kinds != null && kinds.Length > 0)
            {
                var expanded = ExpandCsv(kinds);
                if (expanded.Length > 0)
                {
                    allowedKinds = new HashSet<string>(expanded, StringComparer.Ordinal);
                    kindsRaw = string.Join(",", expanded);
                }
            }

            HashSet<string>? allowedProvenance = null;
            string? provenanceRaw = null;
            if (provenance != null && provenance.Length > 0)
            {
                var expanded = ExpandCsv(provenance);
                if (expanded.Length > 0)
                {
                    allowedProvenance = new HashSet<string>(expanded, StringComparer.Ordinal);
                    provenanceRaw = string.Join(",", expanded);
                }
            }

            var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(store, symbol, snapshotId);

            var fingerprint = SequenceCursor.ComputeFingerprint(
                resolvedSymbolId,
                impactDirection.ToString(),
                maxDepth.ToString(CultureInfo.InvariantCulture),
                kindsRaw,
                provenanceRaw,
                "symbols");

            SequenceCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = SequenceCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);

                try
                {
                    cursorObj.Validate(snapshotId, fingerprint, CursorKind);
                }
                catch (ArgumentException ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }
            }

            var offset = cursorObj?.Offset ?? 0;

            var reachability = new ImpactReachability(store, snapshotId, store);
            var traced = reachability.Trace(resolvedSymbolId, impactDirection, allowedKinds, allowedProvenance, maxDepth, includeSource: true, cancellationToken: cancellationToken);

            var response = ImpactPaging.BuildSymbolsResponse(
                snapshotId,
                _session.GetFreshnessJson(),
                resolvedSymbolId,
                impactDirection,
                maxDepth,
                traced,
                offset,
                limitValue,
                fingerprint,
                CursorKind,
                pinned: true,
                includeLimitEcho: true);

            return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    private static string[] ExpandCsv(string[] values)
    {
        var result = new List<string>();
        foreach (var v in values)
        {
            if (string.IsNullOrWhiteSpace(v))
                continue;
            var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    result.Add(p);
            }
        }

        return result.ToArray();
    }
}
