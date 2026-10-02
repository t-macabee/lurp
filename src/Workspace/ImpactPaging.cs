using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace;

internal sealed record ImpactSymbolGroup(
    string first_hop_source_symbol_id,
    string first_hop_target_symbol_id,
    string edge_kind,
    string provenance,
    int symbol_count,
    int max_depth);

/// <summary>
///     The v2 impact response: reached symbols with witness paths, grouped by
///     first hop. Built once in <see cref="ImpactPaging.BuildSymbolsResponse" />
///     for both the CLI and the MCP tool; the MCP envelope adds only
///     <c>pinned</c> and the page-size echo (<c>limit</c>).
/// </summary>
internal sealed record ImpactResponse(
    string snapshot_id,
    object freshness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? pinned,
    string symbol_id,
    string direction,
    int max_depth,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? limit,
    int symbol_count_total,
    int frontier_count,
    int offset,
    IReadOnlyList<ImpactSymbolGroup> groups,
    IReadOnlyList<object> semantic_causes,
    object? truncated,
    IReadOnlyList<object> symbols);

internal static class ImpactPaging
{
    /// <summary>
    ///     Builds the v2 symbols response. One builder for the CLI and the MCP
    ///     tool: page the core's depth-then-symbol-ID order, group every reached
    ///     symbol by the first hop of its witness path (before the page cut), and
    ///     emit each page symbol's witness path. The caller owns serialization and
    ///     the freshness object, so the two envelopes differ only in
    ///     <c>pinned</c> and the <c>limit</c> echo.
    /// </summary>
    public static ImpactResponse BuildSymbolsResponse(
        string snapshotId,
        object freshness,
        string symbolId,
        ImpactDirection direction,
        int maxDepth,
        ImpactReachabilityResult reachability,
        int offset,
        int limit,
        string fingerprint,
        string cursorKind,
        bool pinned = false,
        bool includeLimitEcho = false)
    {
        var allSymbols = reachability.Symbols;
        var page = allSymbols.Skip(offset).Take(limit).ToList();
        var remaining = Math.Max(0, allSymbols.Count - (offset + page.Count));
        object? truncated = remaining > 0
            ? new
            {
                reason = "limit",
                returned = page.Count,
                total = allSymbols.Count,
                remaining,
                cursor = new SequenceCursor(snapshotId, fingerprint, cursorKind, offset + page.Count).Encode()
            }
            : null;

        return new ImpactResponse(
            snapshot_id: snapshotId,
            freshness: freshness,
            pinned: pinned ? true : null,
            symbol_id: symbolId,
            direction: direction == ImpactDirection.Downstream ? "downstream" : "upstream",
            max_depth: maxDepth,
            limit: includeLimitEcho ? limit : null,
            symbol_count_total: allSymbols.Count,
            frontier_count: allSymbols.Count(static symbol => symbol.IsFrontier),
            offset: offset,
            groups: BuildFirstHopGroups(allSymbols, reachability),
            semantic_causes: reachability.SemanticCauses.Select(ToSemanticCauseJson).ToList(),
            truncated: truncated,
            symbols: page.Select(symbol => ToSymbolJson(symbol, reachability.WitnessPath(symbol.SymbolId))).ToList());
    }

    /// <summary>
    ///     Groups by the first hop of each symbol's witness path over ALL reached
    ///     symbols, before the page cut. The deterministic core order makes the
    ///     first hop of a symbol stable, so the group key is stable too.
    /// </summary>
    private static List<ImpactSymbolGroup> BuildFirstHopGroups(IReadOnlyList<ImpactReachedSymbol> symbols, ImpactReachabilityResult reachability)
    {
        var byFirstHop = new Dictionary<(string Source, string Target, string Kind), (string Provenance, int Count, int MaxDepth)>();
        foreach (var symbol in symbols)
        {
            // Every reached symbol has a non-empty witness path: the anchor is the
            // only symbol without a parent hop and it is not in Symbols.
            var firstHop = reachability.WitnessPath(symbol.SymbolId)[0];
            var key = (firstHop.SourceSymbolId, firstHop.TargetSymbolId, firstHop.EdgeKind);
            if (byFirstHop.TryGetValue(key, out var aggregate))
                byFirstHop[key] = (aggregate.Provenance, aggregate.Count + 1, Math.Max(aggregate.MaxDepth, symbol.Depth));
            else
                byFirstHop[key] = (firstHop.Provenance, 1, symbol.Depth);
        }

        return byFirstHop
            .Select(pair => new ImpactSymbolGroup(
                first_hop_source_symbol_id: pair.Key.Source,
                first_hop_target_symbol_id: pair.Key.Target,
                edge_kind: pair.Key.Kind,
                provenance: pair.Value.Provenance,
                symbol_count: pair.Value.Count,
                max_depth: pair.Value.MaxDepth))
            .OrderByDescending(static group => group.symbol_count)
            .ThenBy(static group => group.first_hop_target_symbol_id, StringComparer.Ordinal)
            .ToList();
    }

    private static object ToSymbolJson(ImpactReachedSymbol symbol, IReadOnlyList<ImpactHop> witnessPath)
    {
        return new
        {
            symbol_id = symbol.SymbolId,
            depth = symbol.Depth,
            shortest_path_count = symbol.ShortestPathCount,
            frontier = symbol.IsFrontier,
            witness_path = new
            {
                total_steps = witnessPath.Count,
                hops = witnessPath.Select(ToHopJson)
            }
        };
    }

    private static object ToHopJson(ImpactHop hop)
    {
        return new
        {
            source_symbol_id = hop.SourceSymbolId,
            target_symbol_id = hop.TargetSymbolId,
            edge_kind = hop.EdgeKind,
            provenance = hop.Provenance,
            source_document = hop.SourceDocument,
            source_line = hop.SourceLine
        };
    }

    private static object ToSemanticCauseJson(SemanticChange cause)
    {
        return new
        {
            from_snapshot_id = cause.FromSnapshotId,
            to_snapshot_id = cause.ToSnapshotId,
            change_type = cause.ChangeType,
            symbol_id = cause.SymbolId,
            detail = cause.DetailJson != null ? JsonSerializer.Deserialize<object>(cause.DetailJson) : null
        };
    }

    /// <summary>
    ///     Total order for cursor stability: first hop, then length, then the full hop chain.
    ///     Paths that share a first hop sort together, so a page never interleaves groups.
    /// </summary>
    internal static string PathKey(ImpactPath path)
    {
        if (path.Hops.Count == 0)
            return string.Empty;

        var first = path.Hops[0];
        var chain = string.Join('>', path.Hops.Select(static hop => $"{hop.SourceSymbolId}-{hop.EdgeKind}->{hop.TargetSymbolId}"));
        return $"{first.SourceSymbolId}\u0001{first.TargetSymbolId}\u0001{first.EdgeKind}\u0001{path.TotalSteps:D4}\u0001{chain}";
    }
}
