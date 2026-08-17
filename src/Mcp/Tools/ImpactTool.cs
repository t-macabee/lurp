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
                throw new McpProtocolException("--symbol is required.", McpErrorCode.InvalidParams);

            var directionArg = string.IsNullOrEmpty(direction) ? "downstream" : direction;
            var impactDirection = directionArg.ToLowerInvariant() switch
            {
                "downstream" => ImpactDirection.Downstream,
                "upstream" => ImpactDirection.Upstream,
                _ => throw new McpProtocolException("--direction must be one of: downstream, upstream.", McpErrorCode.InvalidParams)
            };

            var maxDepth = max_depth ?? DefaultMaxDepth;
            if (maxDepth < 1)
                throw new McpProtocolException("--max-depth must be a positive integer.", McpErrorCode.InvalidParams);

            var maxPaths = max_paths ?? DefaultMaxPaths;
            if (maxPaths < 1)
                throw new McpProtocolException("--max-paths must be a positive integer.", McpErrorCode.InvalidParams);

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
                    throw new McpProtocolException("--cursor is not a valid continuation token.", McpErrorCode.InvalidParams);

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

            var paths = traced.OrderBy(PathKey, StringComparer.Ordinal).ToList();

            var groups = paths
                .Where(static path => path.Hops.Count > 0)
                .GroupBy(static path => (path.Hops[0].SourceSymbolId, path.Hops[0].TargetSymbolId, path.Hops[0].EdgeKind))
                .Select(group => new
                {
                    first_hop_source_symbol_id = group.Key.SourceSymbolId,
                    first_hop_target_symbol_id = group.Key.TargetSymbolId,
                    edge_kind = group.Key.EdgeKind,
                    provenance = group.First().Hops[0].Provenance,
                    path_count = group.Count(),
                    max_total_steps = group.Max(static path => path.TotalSteps)
                })
                .OrderByDescending(static group => group.path_count)
                .ThenBy(static group => group.first_hop_target_symbol_id, StringComparer.Ordinal)
                .ToList();

            var page = paths.Skip(offset).Take(maxPaths).ToList();
            var remaining = Math.Max(0, paths.Count - (offset + page.Count));
            object? truncated = remaining > 0
                ? new
                {
                    reason = "max_paths",
                    returned = page.Count,
                    total = paths.Count,
                    remaining,
                    cursor = new SequenceCursor(snapshotId, fingerprint, CursorKind, offset + page.Count).Encode()
                }
                : null;

            var freshness = _session.GetFreshnessJson();

            var pathJson = page.Select(ToPathJson).ToList();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                symbol_id = resolvedSymbolId,
                direction = impactDirection == ImpactDirection.Downstream ? "downstream" : "upstream",
                max_depth = maxDepth,
                max_paths = maxPaths,
                path_count_total = paths.Count,
                offset,
                groups,
                truncated,
                paths = pathJson
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

    private static object ToPathJson(ImpactPath path)
    {
        return new
        {
            truncated = path.Truncated,
            truncation_reason = path.TruncationReason,
            total_steps = path.TotalSteps,
            hops = path.Hops.Select(static h => new
            {
                source_symbol_id = h.SourceSymbolId,
                target_symbol_id = h.TargetSymbolId,
                edge_kind = h.EdgeKind,
                provenance = h.Provenance,
                source_document = h.SourceDocument,
                source_line = h.SourceLine
            }),
            semantic_causes = path.SemanticCauses.Select(static c => new
            {
                from_snapshot_id = c.FromSnapshotId,
                to_snapshot_id = c.ToSnapshotId,
                change_type = c.ChangeType,
                symbol_id = c.SymbolId,
                detail = c.DetailJson != null ? JsonSerializer.Deserialize<object>(c.DetailJson) : null
            })
        };
    }

    private static string PathKey(ImpactPath path)
    {
        if (path.Hops.Count == 0)
            return string.Empty;

        var first = path.Hops[0];
        var chain = string.Join('>', path.Hops.Select(static hop => $"{hop.SourceSymbolId}-{hop.EdgeKind}->{hop.TargetSymbolId}"));
        return $"{first.SourceSymbolId}\u0001{first.TargetSymbolId}\u0001{first.EdgeKind}\u0001{path.TotalSteps:D4}\u0001{chain}";
    }
}
