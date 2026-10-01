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
    private const int DefaultMaxPaths = 50;

    private readonly McpSessionContext _session;

    public ImpactTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_impact", Title = "Lurp Impact", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Trace impact downstream or upstream for a symbol. Supports kinds/provenance filtering and cursor pagination.")]
    public string LurpImpact(
        string? symbol = null,
        string? direction = null,
        string[]? kinds = null,
        string[]? provenance = null,
        int? max_depth = null,
        int? max_paths = null,
        string? cursor = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

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

            var maxPaths = max_paths ?? DefaultMaxPaths;
            if (maxPaths < 1)
                throw new McpProtocolException("max-paths must be a positive integer.", McpErrorCode.InvalidParams);

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

            var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(_session.Store, symbol, snapshotId);

            var fingerprint = SequenceCursor.ComputeFingerprint(
                resolvedSymbolId,
                impactDirection.ToString(),
                maxDepth.ToString(CultureInfo.InvariantCulture),
                kindsRaw,
                provenanceRaw);

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

            var traverser = new ImpactTraverser(_session.Store, snapshotId, _session.Store);
            var traced = traverser.TraceImpact(resolvedSymbolId, impactDirection, allowedKinds, allowedProvenance, maxDepth);

            var paged = ImpactPaging.Page(traced, offset, maxPaths, snapshotId, fingerprint, CursorKind);

            var freshness = _session.GetFreshnessJson();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                symbol_id = resolvedSymbolId,
                direction = impactDirection == ImpactDirection.Downstream ? "downstream" : "upstream",
                max_depth = maxDepth,
                max_paths = maxPaths,
                path_count_total = paged.TotalPathCount,
                offset,
                groups = paged.Groups,
                truncated = paged.Truncated,
                paths = paged.PathJson
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
