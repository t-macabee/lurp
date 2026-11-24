namespace Lurp.Workspace;

/// <summary>
///     Reachability traversal over the edge graph: reports each reached symbol
///     with its depth, a deterministic witness path and its shortest-path
///     count. It never enumerates one entry per path, so memory stays
///     O(symbols + edges examined) instead of growing exponentially with
///     <c>maxDepth</c>.
/// </summary>
public sealed class ImpactReachability
{
    [Flags]
    internal enum MemberAccess { None = 0, Read = 1, Write = 2 }

    internal readonly record struct Visit(string SymbolId, MemberAccess Access = MemberAccess.None);

    internal sealed record Witness(Visit Parent, ImpactHop Hop, MemberAccess ParentAccess,
        (ImpactHop Hop, MemberAccess ParentAccess)? Read, (ImpactHop Hop, MemberAccess ParentAccess)? Write)
    {
        internal (ImpactHop Hop, MemberAccess ParentAccess) HopFor(MemberAccess access) => access switch
        {
            MemberAccess.Read when Read.HasValue => Read.Value,
            MemberAccess.Write when Write.HasValue => Write.Value,
            _ => (Hop, ParentAccess)
        };
    }

    private readonly record struct Expansion(EdgeRecord Edge, MemberAccess ParentAccess = MemberAccess.None);

    private readonly Dictionary<string, List<(string SymbolId, string AccessorKind)>> _accessorCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(ImpactDirection, string), List<EdgeRecord>> _edgeCache = [];
    private readonly string _snapshotId;
    private readonly IEdgeStore _store;
    private readonly IDeclarationStore? _declarations;

    public ImpactReachability(IEdgeStore store, string snapshotId) : this(store, snapshotId, store as IDeclarationStore)
    {
    }

    public ImpactReachability(IEdgeStore store, string snapshotId, IDeclarationStore? declarations)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _declarations = declarations;
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
    }

    internal static readonly IReadOnlySet<string> DefaultTraversalKinds = Enum.GetNames<EdgeKind>()
        .Where(name => name is not nameof(EdgeKind.Declares) and not nameof(EdgeKind.Contains) and not nameof(EdgeKind.TestedBy))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Computes the reached-symbol set with a level-by-level BFS. The anchor
    ///     sits at depth 0 and is not part of the result.
    ///     <para>
    ///     Shortest-path counts count shortest symbol routes: parallel edges
    ///     between the same symbol pair (for example <c>Calls</c> and
    ///     <c>StaticallyCalls</c> on one static call site) count once. The
    ///     sum saturates at <see cref="long.MaxValue" /> instead of overflowing.
    ///     </para>
    /// </summary>
    public ImpactReachabilityResult Trace(string symbolId, ImpactDirection direction, int maxDepth,
        IReadOnlySet<string>? allowedEdgeKinds = null, HashSet<string>? allowedProvenance = null,
        bool includeSource = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(symbolId))
            throw new ArgumentException("Symbol id is required.", nameof(symbolId));

        cancellationToken.ThrowIfCancellationRequested();
        var seeds = maxDepth > 0
            ? GetSeeds(symbolId, direction, allowedEdgeKinds)
            : new HashSet<string>(StringComparer.Ordinal) { symbolId };
        var depth = seeds.ToDictionary(id => new Visit(id), _ => 0);
        var parents = new Dictionary<Visit, Witness>();
        var pathCounts = depth.Keys.ToDictionary(visit => visit, _ => 1L);
        var level = depth.Keys.ToList();

        for (var currentDepth = 0; currentDepth < maxDepth && level.Count > 0; currentDepth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = new List<Visit>();

            foreach (var current in level.OrderBy(visit => visit.SymbolId, StringComparer.Ordinal).ThenBy(visit => visit.Access))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var edges = FilteredExpansions(current, direction, allowedEdgeKinds, allowedProvenance).ToList();
                edges.Sort((left, right) =>
                {
                    var order = CompareForExpansion(left.Edge, right.Edge, direction);
                    return order != 0 ? order : left.ParentAccess.CompareTo(right.ParentAccess);
                });

                foreach (var group in edges.GroupBy(expansion => NeighborOf(expansion.Edge, direction), StringComparer.Ordinal))
                {
                    if (seeds.Contains(group.Key))
                        continue;

                    // A visit records the union of access modes available from this parent.
                    // A compound assignment still contributes one symbol route.
                    var access = MemberAccess.None;
                    Expansion? read = null, write = null;
                    if (direction == ImpactDirection.Downstream && _declarations != null &&
                        SymbolId.DocCommentIdOf(group.Key) is [('P' or 'E'), ':', ..])
                    {
                        foreach (var expansion in group)
                        {
                            if (expansion.Edge.Kind == nameof(EdgeKind.Reads))
                                read ??= expansion;
                            else if (expansion.Edge.Kind == nameof(EdgeKind.Writes))
                                write ??= expansion;
                        }

                        if (read.HasValue) access |= MemberAccess.Read;
                        if (write.HasValue) access |= MemberAccess.Write;
                    }

                    var neighbor = new Visit(group.Key, access);
                    if (depth.TryGetValue(neighbor, out var neighborDepth))
                    {
                        if (neighborDepth == currentDepth + 1)
                            pathCounts[neighbor] = SaturatingAdd(pathCounts[neighbor], pathCounts[current]);
                        continue;
                    }

                    var first = group.First();
                    depth[neighbor] = currentDepth + 1;
                    parents[neighbor] = new Witness(current, BuildHop(first.Edge, includeSource), first.ParentAccess,
                        read is { } readEdge ? (BuildHop(readEdge.Edge, includeSource), readEdge.ParentAccess) : null,
                        write is { } writeEdge ? (BuildHop(writeEdge.Edge, includeSource), writeEdge.ParentAccess) : null);
                    pathCounts[neighbor] = pathCounts[current];
                    next.Add(neighbor);
                }
            }

            level = next;
        }

        var symbols = new List<ImpactReachedSymbol>();
        var witnesses = new Dictionary<string, Visit>(StringComparer.Ordinal);
        foreach (var visits in parents.Keys.OrderBy(visit => depth[visit]).ThenBy(visit => visit.SymbolId, StringComparer.Ordinal)
                     .ThenBy(visit => visit.Access)
                     .GroupBy(visit => visit.SymbolId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var visit = visits.First();
            var shortest = depth[visit];
            var count = visits.TakeWhile(candidate => depth[candidate] == shortest)
                .Aggregate(0L, (total, candidate) => SaturatingAdd(total, pathCounts[candidate]));
            var isFrontier = shortest == maxDepth &&
                             visits.TakeWhile(candidate => depth[candidate] == shortest)
                                 .Any(candidate => HasFilteredEdgeOffWitnessPath(candidate, direction, seeds, parents,
                                     allowedEdgeKinds, allowedProvenance));
            symbols.Add(new ImpactReachedSymbol(visit.SymbolId, shortest, count, isFrontier, parents[visit].Hop));
            witnesses.Add(visit.SymbolId, visit);
        }

        return new ImpactReachabilityResult(symbolId, symbols, seeds, witnesses, parents);
    }

    private HashSet<string> GetSeeds(string symbolId, ImpactDirection direction, IReadOnlySet<string>? allowedEdgeKinds)
    {
        var seeds = new HashSet<string>(StringComparer.Ordinal) { symbolId };
        if (direction != ImpactDirection.Downstream)
            return seeds;

        switch (SymbolId.DocCommentIdOf(symbolId))
        {
            case ['T', ':', ..] when allowedEdgeKinds != null &&
                                     !allowedEdgeKinds.Contains(nameof(EdgeKind.Declares)):
                seeds.UnionWith(GetEdges(symbolId, direction)
                    .Where(edge => edge.Kind == nameof(EdgeKind.Declares))
                    .Select(edge => edge.TargetSymbolId));
                break;
            case [('P' or 'E'), ':', ..]:
                seeds.UnionWith(GetAccessors(symbolId).Select(accessor => accessor.SymbolId));
                break;
        }

        return seeds;
    }

    private List<(string SymbolId, string AccessorKind)> GetAccessors(string symbolId)
    {
        if (!_accessorCache.TryGetValue(symbolId, out var accessors))
            _accessorCache[symbolId] = accessors = _declarations?.GetAccessorsForSymbol(_snapshotId, symbolId) ?? [];
        return accessors;
    }

    private List<EdgeRecord> GetEdges(string currentId, ImpactDirection direction)
    {
        if (_edgeCache.TryGetValue((direction, currentId), out var cached))
            return cached;

        var edges = direction switch
        {
            ImpactDirection.Downstream => _store.GetOutgoingEdges(_snapshotId, currentId),
            ImpactDirection.Upstream => _store.GetIncomingEdges(_snapshotId, currentId),
            _ => throw new InvalidOperationException("Unknown impact direction")
        };
        _edgeCache[(direction, currentId)] = edges;
        return edges;
    }

    private bool HasFilteredEdgeOffWitnessPath(Visit visit, ImpactDirection direction, HashSet<string> seeds,
        Dictionary<Visit, Witness> parents, IReadOnlySet<string>? allowedEdgeKinds, HashSet<string>? allowedProvenance)
    {
        var witnessSymbols = new HashSet<string>(StringComparer.Ordinal) { visit.SymbolId };
        var current = visit;
        while (parents.TryGetValue(current, out var witness))
        {
            current = witness.Parent;
            witnessSymbols.Add(current.SymbolId);
        }

        return FilteredExpansions(visit, direction, allowedEdgeKinds, allowedProvenance).Any(expansion =>
        {
            var neighbor = NeighborOf(expansion.Edge, direction);
            return !witnessSymbols.Contains(neighbor) && !seeds.Contains(neighbor);
        });
    }

    private IEnumerable<Expansion> FilteredExpansions(Visit visit, ImpactDirection direction,
        IReadOnlySet<string>? allowedEdgeKinds, HashSet<string>? allowedProvenance) =>
        Expand(visit, direction).Where(expansion =>
            (allowedEdgeKinds == null || allowedEdgeKinds.Contains(expansion.Edge.Kind)) &&
            (allowedProvenance == null || allowedProvenance.Contains(expansion.Edge.Provenance)));

    private IEnumerable<Expansion> Expand(Visit visit, ImpactDirection direction)
    {
        var id = visit.SymbolId;
        foreach (var edge in GetEdges(id, direction))
            yield return new Expansion(edge);

        if (_declarations == null)
            yield break;

        if (direction == ImpactDirection.Downstream && visit.Access != MemberAccess.None)
        {
            foreach (var (accessor, role) in GetAccessors(id))
            {
                var access = MatchingAccess(visit.Access, role);
                if (access == MemberAccess.None)
                    continue;

                foreach (var edge in GetEdges(accessor, direction))
                    if (edge.TargetSymbolId != id)
                        yield return new Expansion(edge, access);
            }
        }
        else if (direction == ImpactDirection.Upstream && SymbolId.DocCommentIdOf(id) is ['M', ':', ..])
        {
            var metadata = SymbolMetadata.Parse(_declarations.GetSymbolInfo(id, _snapshotId)?.MetadataJson, id);
            var role = SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AccessorKind);
            var associatedDocId = SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AssociatedSymbol);
            if (role == null || associatedDocId == null || !SymbolId.TryParse(id, out var parsed))
                yield break;

            var associatedId = SymbolId.Compose(associatedDocId, parsed.AssemblyIdentity);
            var family = GetAccessors(associatedId);
            foreach (var edge in GetEdges(associatedId, direction))
                if (edge.SourceSymbolId != id && edge.SourceSymbolId != associatedId &&
                    AccessorEdges.Inherits(role, edge.Kind) && !family.Any(accessor => accessor.SymbolId == edge.SourceSymbolId))
                    yield return new Expansion(edge);
        }
    }

    private static MemberAccess MatchingAccess(MemberAccess access, string role)
    {
        if ((access & MemberAccess.Read) != 0 && AccessorEdges.Inherits(role, nameof(EdgeKind.Reads)))
            return MemberAccess.Read;
        if ((access & MemberAccess.Write) != 0 && AccessorEdges.Inherits(role, nameof(EdgeKind.Writes)))
            return MemberAccess.Write;
        return MemberAccess.None;
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

        compared = Nullable.Compare(left.SourceStartColumn, right.SourceStartColumn);
        if (compared != 0) return compared;

        // A bridged edge can share a neighbor and location with an edge from
        // another accessor. Use both persisted endpoints to break those ties.
        compared = string.CompareOrdinal(left.SourceSymbolId, right.SourceSymbolId);
        return compared != 0 ? compared : string.CompareOrdinal(left.TargetSymbolId, right.TargetSymbolId);
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

    private static long SaturatingAdd(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    /// <summary>
    ///     Loads the ordered sites from <c>edge_sites</c> for every located hop in one
    ///     query and sets <see cref="ImpactHop.SiteCount" /> and <see cref="ImpactHop.Sites" />
    ///     (capped at <see cref="ImpactHop.MaxSitesPerHop" />, 1-based lines, D155.7).
    /// </summary>
    public void PopulateSites(IEnumerable<ImpactHop> hops)
    {
        var hopList = hops as IReadOnlyList<ImpactHop> ?? [.. hops];
        var triples = new HashSet<(string Source, string Target, string Kind)>();
        foreach (var hop in hopList)
            if (hop.SourceDocument != null)
                triples.Add((hop.SourceSymbolId, hop.TargetSymbolId, hop.EdgeKind));

        if (triples.Count == 0)
            return;

        var sitesByTriple = _store.GetEdgeSites(_snapshotId, triples);
        foreach (var hop in hopList)
        {
            if (hop.SourceDocument == null)
                continue;

            if (!sitesByTriple.TryGetValue((hop.SourceSymbolId, hop.TargetSymbolId, hop.EdgeKind), out var sites) || sites.Count == 0)
                continue;

            hop.SiteCount = sites.Count;
            hop.Sites = sites
                .Take(ImpactHop.MaxSitesPerHop)
                .Select(static s => new ImpactHopSite(
                    s.DocumentPath,
                    LineNumbers.ToOneBased(s.StartLine),
                    s.StartColumn,
                    LineNumbers.ToOneBased(s.EndLine),
                    s.EndColumn))
                .ToList();
        }
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
    ///     Number of shortest-length symbol routes from the anchor to this symbol.
    ///     Parallel edges between two symbols count once. Saturates at
    ///     <see cref="long.MaxValue" />.
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
    private readonly IReadOnlySet<string> _seeds;
    private readonly IReadOnlyDictionary<string, ImpactReachability.Visit> _witnesses;
    private readonly IReadOnlyDictionary<ImpactReachability.Visit, ImpactReachability.Witness> _parents;

    internal ImpactReachabilityResult(string anchorSymbolId, IReadOnlyList<ImpactReachedSymbol> symbols,
        IReadOnlySet<string> seeds, IReadOnlyDictionary<string, ImpactReachability.Visit> witnesses,
        IReadOnlyDictionary<ImpactReachability.Visit, ImpactReachability.Witness> parents)
    {
        AnchorSymbolId = anchorSymbolId ?? throw new ArgumentNullException(nameof(anchorSymbolId));
        Symbols = symbols ?? throw new ArgumentNullException(nameof(symbols));
        _seeds = seeds;
        _witnesses = witnesses;
        _parents = parents;
    }

    public string AnchorSymbolId { get; }

    /// <summary>Reached symbols (anchor excluded), sorted by depth, then symbol ID (ordinal).</summary>
    public IReadOnlyList<ImpactReachedSymbol> Symbols { get; }

    /// <summary>
    ///     The witness path from a depth-zero starting point to <paramref name="symbolId" />,
    ///     in traversal order (first hop first). Empty for the starting points and
    ///     for symbols that were not reached.
    /// </summary>
    public List<ImpactHop> WitnessPath(string symbolId)
    {
        ArgumentNullException.ThrowIfNull(symbolId);

        if (!_witnesses.TryGetValue(symbolId, out var current))
            return [];

        var hops = new List<ImpactHop>();
        var access = ImpactReachability.MemberAccess.None;
        while (!_seeds.Contains(current.SymbolId))
        {
            var witness = _parents[current];
            var (hop, parentAccess) = witness.HopFor(access);
            hops.Add(hop);
            access = parentAccess;
            current = witness.Parent;
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
        var parents = new HashSet<ImpactReachability.Visit>();
        foreach (var visit in _witnesses.Values)
        {
            var current = visit;
            while (_parents.TryGetValue(current, out var witness) && parents.Add(witness.Parent))
                current = witness.Parent;
        }

        var leaves = new List<ImpactPath>();
        foreach (var symbol in Symbols)
        {
            if (parents.Contains(_witnesses[symbol.SymbolId]))
                continue;

            leaves.Add(new ImpactPath(WitnessPath(symbol.SymbolId),
                symbol.IsFrontier,
                symbol.IsFrontier ? "max depth reached" : null));
        }

        return leaves
            .OrderBy(path => path.TotalSteps)
            .ThenBy(ImpactPaging.PathKey, StringComparer.Ordinal)
            .ToList();
    }

}
