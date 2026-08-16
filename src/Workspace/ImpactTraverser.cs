namespace Lurp.Workspace;

public sealed class ImpactTraverser
{
    private readonly ISemanticDiffStore? _semanticDiffStore;
    private readonly string _snapshotId;
    private readonly IEdgeStore _store;

    public ImpactTraverser(IEdgeStore store, string snapshotId, ISemanticDiffStore? semanticDiffStore = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshotId = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
        _semanticDiffStore = semanticDiffStore;
    }

    public List<ImpactPath> TraceImpact(string symbolId, ImpactDirection direction, HashSet<string>? allowedEdgeKinds = null, HashSet<string>? allowedProvenance = null, int maxDepth = 10, bool includeSource = true)
    {
        var results = new List<ImpactPath>();
        var semanticCauses = GetSemanticCauses(symbolId);

        var queue = new Queue<(string currentId, List<ImpactHop> hops, HashSet<string> visited)>();
        queue.Enqueue((symbolId, [], [symbolId]));

        while (queue.Count > 0)
        {
            var (currentId, hopsSoFar, visited) = queue.Dequeue();

            if (hopsSoFar.Count >= maxDepth)
            {
                results.Add(new ImpactPath(hopsSoFar, true, "max depth reached", semanticCauses));
                continue;
            }

            if (!TryGetEdges(currentId, direction, out var edges))
                continue;

            if (edges.Count == 0 && hopsSoFar.Count > 0)
            {
                results.Add(new ImpactPath(hopsSoFar, semanticCauses: semanticCauses));
                continue;
            }

            var anyEdgeFollowed = EnqueueNeighbors(queue, edges, direction, allowedEdgeKinds, allowedProvenance, visited, hopsSoFar, includeSource);

            if (!anyEdgeFollowed && hopsSoFar.Count > 0) results.Add(new ImpactPath(hopsSoFar, semanticCauses: semanticCauses));
        }

        return results;
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
            Console.Error.WriteLine($"WARNING: ImpactTraverser: failed to retrieve semantic changes for snapshot '{_snapshotId}': {ex.Message}");
            return [];
        }
    }

    private bool TryGetEdges(string currentId, ImpactDirection direction, out List<EdgeRecord> edges)
    {
        try
        {
            edges = direction switch
            {
                ImpactDirection.Downstream => _store.GetOutgoingEdges(_snapshotId, currentId),
                ImpactDirection.Upstream => _store.GetIncomingEdges(_snapshotId, currentId),
                _ => []
            };
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: ImpactTraverser: failed to retrieve edges for symbol '{currentId}' in snapshot '{_snapshotId}': {ex.Message}");
            edges = [];
            return false;
        }
    }

    private static bool EnqueueNeighbors(Queue<(string currentId, List<ImpactHop> hops, HashSet<string> visited)> queue, List<EdgeRecord> edges,
        ImpactDirection direction, HashSet<string>? allowedEdgeKinds, HashSet<string>? allowedProvenance, HashSet<string> visited, List<ImpactHop> hopsSoFar, bool includeSource)
    {
        var anyEdgeFollowed = false;

        foreach (var edge in edges)
        {
            if (allowedEdgeKinds != null && !allowedEdgeKinds.Contains(edge.Kind))
                continue;
            if (allowedProvenance != null && !allowedProvenance.Contains(edge.Provenance))
                continue;

            var neighborId = direction switch
            {
                ImpactDirection.Downstream => edge.TargetSymbolId,
                ImpactDirection.Upstream => edge.SourceSymbolId,
                _ => throw new InvalidOperationException("Unknown impact direction")
            };

            if (visited.Contains(neighborId))
                continue;

            anyEdgeFollowed = true;

            var newHop = new ImpactHop(edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind, edge.Provenance,
                includeSource ? edge.SourceDocumentPath : null,
                // Edges persist Roslyn-native 0-based lines; convert at the emit
                // boundary through the LineNumbers choke point so the hop a
                // consumer reads is 1-based (matching --line=).
                includeSource ? LineNumbers.ToOneBased(edge.SourceStartLine) : null,
                includeSource ? edge.SourceStartColumn : null,
                includeSource ? LineNumbers.ToOneBased(edge.SourceEndLine) : null,
                includeSource ? edge.SourceEndColumn : null);

            var newHops = new List<ImpactHop>(hopsSoFar) { newHop };
            var newVisited = new HashSet<string>(visited) { neighborId };

            queue.Enqueue((neighborId, newHops, newVisited));
        }

        return anyEdgeFollowed;
    }
}