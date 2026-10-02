namespace Lurp.Workspace;

/// <summary>
///     Reachability traversal over the edge graph: visits each symbol once and
///     reports its depth, a deterministic witness path and its shortest-path
///     count. Unlike <see cref="ImpactTraverser" /> it never enumerates one
///     entry per path, so memory stays O(symbols + edges examined) instead of
///     growing exponentially with <c>maxDepth</c>.
/// </summary>
public sealed class ImpactReachability
{
    private readonly Dictionary<(ImpactDirection, string), List<EdgeRecord>> _edgeCache = [];
    private readonly IOutputSink _output;
    private readonly ISemanticDiffStore? _semanticDiffStore;
    private readonly string _snapshotId;
    private readonly IEdgeStore _store;

    public ImpactReachability(IEdgeStore store, string snapshotId, ISemanticDiffStore? semanticDiffStore = null, IOutputSink? output = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        _semanticDiffStore = semanticDiffStore;
        _output = output ?? ConsoleOutputSink.Instance;
    }

    /// <summary>
    ///     Computes the reached-symbol set with a level-by-level BFS. The anchor
    ///     sits at depth 0 and is not part of the result.
    ///     <para>
    ///     Shortest-path counts count edge rows, not distinct neighbors: two
    ///     edges between the same pair (for example <c>Calls</c> and
    ///     <c>StaticallyCalls</c> on one static call site) are two routes. The
    ///     sum saturates at <see cref="long.MaxValue" /> instead of overflowing.
    ///     </para>
    /// </summary>
    public ImpactReachabilityResult Trace(string symbolId, ImpactDirection direction, HashSet<string>? allowedEdgeKinds = null,
        HashSet<string>? allowedProvenance = null, int maxDepth = 10, bool includeSource = true)
    {
        if (string.IsNullOrEmpty(symbolId))
            throw new ArgumentException("Symbol id is required.", nameof(symbolId));

        var semanticCauses = GetSemanticCauses(symbolId);
        var depth = new Dictionary<string, int>(StringComparer.Ordinal) { [symbolId] = 0 };
        var parentHops = new Dictionary<string, ImpactHop>(StringComparer.Ordinal);
        var pathCounts = new Dictionary<string, long>(StringComparer.Ordinal) { [symbolId] = 1 };
        var reached = new List<string>();
        var level = new List<string> { symbolId };

        for (var currentDepth = 0; currentDepth < maxDepth && level.Count > 0; currentDepth++)
        {
            // One level is expanded at a time, in ordinal symbol order, so the
            // witness parent of a symbol never depends on discovery order.
            level.Sort(StringComparer.Ordinal);
            var next = new List<string>();

            foreach (var currentId in level)
            {
                if (!TryGetEdges(currentId, direction, out var edges))
                    continue;

                foreach (var edge in FilterAndSort(edges, direction, allowedEdgeKinds, allowedProvenance))
                {
                    var neighborId = NeighborOf(edge, direction);

                    if (depth.TryGetValue(neighborId, out var neighborDepth))
                    {
                        // An edge from the previous level is one more shortest
                        // route; an edge into an already shallower symbol cannot
                        // extend a shortest path to it.
                        if (neighborDepth == currentDepth + 1)
                            pathCounts[neighborId] = SaturatingAdd(pathCounts[neighborId], pathCounts[currentId]);
                        continue;
                    }

                    depth[neighborId] = currentDepth + 1;
                    parentHops[neighborId] = BuildHop(edge, includeSource);
                    pathCounts[neighborId] = pathCounts[currentId];
                    reached.Add(neighborId);
                    next.Add(neighborId);
                }
            }

            level = next;
        }

        var symbols = new List<ImpactReachedSymbol>(reached.Count);
        foreach (var id in reached.OrderBy(id => depth[id]).ThenBy(id => id, StringComparer.Ordinal))
        {
            var isFrontier = depth[id] == maxDepth &&
                             HasFilteredEdgeOffWitnessPath(id, direction, symbolId, parentHops, allowedEdgeKinds, allowedProvenance);
            symbols.Add(new ImpactReachedSymbol(id, depth[id], pathCounts[id], isFrontier, parentHops[id]));
        }

        return new ImpactReachabilityResult(symbolId, symbols, semanticCauses, parentHops);
    }

    private List<SemanticChange> GetSemanticCauses(string symbolId)
    {
        if (_semanticDiffStore == null)
            return [];

        try
        {
            return [.. _semanticDiffStore.GetSemanticChangesToSnapshot(_snapshotId)
                .Where(change => change.SymbolId == symbolId)];
        }
        catch (Exception ex)
        {
            _output.WriteErrorLine($"WARNING: ImpactReachability: failed to retrieve semantic changes for snapshot '{_snapshotId}': {ex.Message}");
            return [];
        }
    }

    private bool TryGetEdges(string currentId, ImpactDirection direction, out List<EdgeRecord> edges)
    {
        if (_edgeCache.TryGetValue((direction, currentId), out var cached))
        {
            edges = cached;
            return true;
        }

        try
        {
            edges = direction switch
            {
                ImpactDirection.Downstream => _store.GetOutgoingEdges(_snapshotId, currentId),
                ImpactDirection.Upstream => _store.GetIncomingEdges(_snapshotId, currentId),
                _ => []
            };
            _edgeCache[(direction, currentId)] = edges;
            return true;
        }
        catch (Exception ex)
        {
            _output.WriteErrorLine($"WARNING: ImpactReachability: failed to retrieve edges for symbol '{currentId}' in snapshot '{_snapshotId}': {ex.Message}");
            edges = [];
            return false;
        }
    }

    private bool HasFilteredEdgeOffWitnessPath(string symbolId, ImpactDirection direction, string anchorId,
        IReadOnlyDictionary<string, ImpactHop> parentHops, HashSet<string>? allowedEdgeKinds, HashSet<string>? allowedProvenance)
    {
        if (!TryGetEdges(symbolId, direction, out var edges))
            return false;

        var witnessSymbols = WitnessSymbols(symbolId, anchorId, parentHops);
        foreach (var edge in FilteredEdges(edges, allowedEdgeKinds, allowedProvenance))
            if (!witnessSymbols.Contains(NeighborOf(edge, direction)))
                return true;
        return false;
    }

    private static HashSet<string> WitnessSymbols(string symbolId, string anchorId, IReadOnlyDictionary<string, ImpactHop> parentHops)
    {
        var symbols = new HashSet<string>(StringComparer.Ordinal) { symbolId };
        var current = symbolId;
        while (!string.Equals(current, anchorId, StringComparison.Ordinal) && parentHops.TryGetValue(current, out var hop))
        {
            current = ParentOf(hop, current);
            symbols.Add(current);
        }

        return symbols;
    }

    private static List<EdgeRecord> FilterAndSort(List<EdgeRecord> edges, ImpactDirection direction,
        HashSet<string>? allowedEdgeKinds, HashSet<string>? allowedProvenance)
    {
        var filtered = FilteredEdges(edges, allowedEdgeKinds, allowedProvenance);
        filtered.Sort((left, right) => CompareForExpansion(left, right, direction));
        return filtered;
    }

    private static List<EdgeRecord> FilteredEdges(List<EdgeRecord> edges, HashSet<string>? allowedEdgeKinds,
        HashSet<string>? allowedProvenance)
    {
        var filtered = new List<EdgeRecord>(edges.Count);
        foreach (var edge in edges)
        {
            if (allowedEdgeKinds != null && !allowedEdgeKinds.Contains(edge.Kind))
                continue;
            if (allowedProvenance != null && !allowedProvenance.Contains(edge.Provenance))
                continue;
            filtered.Add(edge);
        }

        return filtered;
    }

    // Expansion order: neighbor, kind, provenance, then source location. The
    // persisted edge_id is deliberately not used: it follows insert order and
    // can change after a reindex.
    private static int CompareForExpansion(EdgeRecord left, EdgeRecord right, ImpactDirection direction)
    {
        var compared = string.CompareOrdinal(NeighborOf(left, direction), NeighborOf(right, direction));
        if (compared != 0) return compared;

        compared = string.CompareOrdinal(left.Kind, right.Kind);
        if (compared != 0) return compared;

        compared = string.CompareOrdinal(left.Provenance, right.Provenance);
        if (compared != 0) return compared;

        compared = string.CompareOrdinal(left.SourceDocumentPath, right.SourceDocumentPath);
        if (compared != 0) return compared;

        compared = Nullable.Compare(left.SourceStartLine, right.SourceStartLine);
        if (compared != 0) return compared;

        return Nullable.Compare(left.SourceStartColumn, right.SourceStartColumn);
    }

    private static ImpactHop BuildHop(EdgeRecord edge, bool includeSource)
    {
        return new ImpactHop(edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind, edge.Provenance,
            includeSource ? edge.SourceDocumentPath : null,
            // Edges persist Roslyn-native 0-based lines; convert at the emit
            // boundary through the LineNumbers choke point so the hop a
            // consumer reads is 1-based (matching --line=).
            includeSource ? LineNumbers.ToOneBased(edge.SourceStartLine) : null,
            includeSource ? edge.SourceStartColumn : null,
            includeSource ? LineNumbers.ToOneBased(edge.SourceEndLine) : null,
            includeSource ? edge.SourceEndColumn : null);
    }

    private static string NeighborOf(EdgeRecord edge, ImpactDirection direction)
    {
        return direction switch
        {
            ImpactDirection.Downstream => edge.TargetSymbolId,
            ImpactDirection.Upstream => edge.SourceSymbolId,
            _ => throw new InvalidOperationException("Unknown impact direction")
        };
    }

    private static string ParentOf(ImpactHop hop, string symbolId)
    {
        return string.Equals(hop.SourceSymbolId, symbolId, StringComparison.Ordinal)
            ? hop.TargetSymbolId
            : hop.SourceSymbolId;
    }

    private static long SaturatingAdd(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }
}

public sealed class ImpactReachedSymbol
{
    public ImpactReachedSymbol(string symbolId, int depth, long shortestPathCount, bool isFrontier, ImpactHop parentHop)
    {
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        Depth = depth;
        ShortestPathCount = shortestPathCount;
        IsFrontier = isFrontier;
        ParentHop = parentHop ?? throw new ArgumentNullException(nameof(parentHop));
    }

    public string SymbolId { get; }

    public int Depth { get; }

    /// <summary>
    ///     Number of shortest-length routes from the anchor to this symbol.
    ///     Routes count edge rows, not distinct neighbors; two edges between the
    ///     same pair are two routes. Saturates at <see cref="long.MaxValue" />.
    /// </summary>
    public long ShortestPathCount { get; }

    /// <summary>
    ///     True when this symbol sits at the depth bound and has at least one
    ///     filtered edge to a symbol outside its own witness path.
    /// </summary>
    public bool IsFrontier { get; }

    public ImpactHop ParentHop { get; }
}

public sealed class ImpactReachabilityResult
{
    private readonly IReadOnlyDictionary<string, ImpactHop> _parentHops;

    internal ImpactReachabilityResult(string anchorSymbolId, IReadOnlyList<ImpactReachedSymbol> symbols,
        List<SemanticChange> semanticCauses, IReadOnlyDictionary<string, ImpactHop> parentHops)
    {
        AnchorSymbolId = anchorSymbolId ?? throw new ArgumentNullException(nameof(anchorSymbolId));
        Symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        SemanticCauses = semanticCauses ?? throw new ArgumentNullException(nameof(semanticCauses));
        _parentHops = parentHops ?? throw new ArgumentNullException(nameof(parentHops));
    }

    public string AnchorSymbolId { get; }

    /// <summary>Reached symbols (anchor excluded), sorted by depth, then symbol ID (ordinal).</summary>
    public IReadOnlyList<ImpactReachedSymbol> Symbols { get; }

    public List<SemanticChange> SemanticCauses { get; }

    /// <summary>
    ///     The witness path from the anchor to <paramref name="symbolId" />, in
    ///     traversal order (first hop first). Empty for the anchor and for
    ///     symbols that were not reached.
    /// </summary>
    public List<ImpactHop> WitnessPath(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);

        var hops = new List<ImpactHop>();
        var current = symbolId;
        while (!string.Equals(current, AnchorSymbolId, StringComparison.Ordinal))
        {
            if (!_parentHops.TryGetValue(current, out var hop))
                return [];
            hops.Add(hop);
            current = ParentOf(hop, current);
        }

        hops.Reverse();
        return hops;
    }

    /// <summary>
    ///     One <see cref="ImpactPath" /> per reached symbol that is not the parent
    ///     of another reached symbol: the leaves of the witness tree, so no
    ///     emitted path is a prefix of another. A leaf at the depth bound whose
    ///     filtered edges leave its witness path carries
    ///     <c>truncated = true</c> and <c>truncation_reason = "max depth reached"</c>.
    /// </summary>
    public IReadOnlyList<ImpactPath> WitnessLeafPaths()
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in Symbols)
            parents.Add(ParentOf(symbol.ParentHop, symbol.SymbolId));

        var leaves = new List<ImpactPath>();
        foreach (var symbol in Symbols)
        {
            if (parents.Contains(symbol.SymbolId))
                continue;

            leaves.Add(new ImpactPath(WitnessPath(symbol.SymbolId),
                symbol.IsFrontier,
                symbol.IsFrontier ? "max depth reached" : null,
                SemanticCauses));
        }

        return leaves
            .OrderBy(path => path.TotalSteps)
            .ThenBy(ImpactPaging.PathKey, StringComparer.Ordinal)
            .ToList();
    }

    private static string ParentOf(ImpactHop hop, string symbolId)
    {
        return string.Equals(hop.SourceSymbolId, symbolId, StringComparison.Ordinal)
            ? hop.TargetSymbolId
            : hop.SourceSymbolId;
    }
}
