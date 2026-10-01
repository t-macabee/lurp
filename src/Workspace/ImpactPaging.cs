using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Workspace;

internal sealed record ImpactGroup(
    string first_hop_source_symbol_id,
    string first_hop_target_symbol_id,
    string edge_kind,
    string provenance,
    int path_count,
    int max_total_steps);

internal sealed record ImpactPagingResult(
    int TotalPathCount,
    IReadOnlyList<ImpactGroup> Groups,
    object? Truncated,
    IReadOnlyList<object> PathJson);

internal static class ImpactPaging
{
    public static ImpactPagingResult Page(
        IReadOnlyCollection<ImpactPath> traced,
        int offset,
        int maxPaths,
        string snapshotId,
        string fingerprint,
        string cursorKind)
    {
        var paths = traced.OrderBy(PathKey, StringComparer.Ordinal).ToList();

        var groups = paths
            .Where(static path => path.Hops.Count > 0)
            .GroupBy(static path => (path.Hops[0].SourceSymbolId, path.Hops[0].TargetSymbolId, path.Hops[0].EdgeKind))
            .Select(group => new ImpactGroup(
                first_hop_source_symbol_id: group.Key.SourceSymbolId,
                first_hop_target_symbol_id: group.Key.TargetSymbolId,
                edge_kind: group.Key.EdgeKind,
                provenance: group.First().Hops[0].Provenance,
                path_count: group.Count(),
                max_total_steps: group.Max(static path => path.TotalSteps)
            ))
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
                cursor = new SequenceCursor(snapshotId, fingerprint, cursorKind, offset + page.Count).Encode()
            }
            : null;

        var pathJson = page.Select(ToPathJson).ToList();

        return new ImpactPagingResult(paths.Count, groups, truncated, pathJson);
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

    /// <summary>
    ///     Total order for cursor stability: first hop, then length, then the full hop chain.
    ///     Paths that share a first hop sort together, so a page never interleaves groups.
    /// </summary>
    private static string PathKey(ImpactPath path)
    {
        if (path.Hops.Count == 0)
            return string.Empty;

        var first = path.Hops[0];
        var chain = string.Join('>', path.Hops.Select(static hop => $"{hop.SourceSymbolId}-{hop.EdgeKind}->{hop.TargetSymbolId}"));
        return $"{first.SourceSymbolId}\u0001{first.TargetSymbolId}\u0001{first.EdgeKind}\u0001{path.TotalSteps:D4}\u0001{chain}";
    }
}
