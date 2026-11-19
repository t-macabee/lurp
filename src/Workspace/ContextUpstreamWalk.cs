namespace Lurp.Workspace;

internal sealed class ContextUpstreamWalk
{
    private readonly record struct Visit(string SymbolId, bool IsDispatchSource = false);

    private sealed record Witness(string ParentId, EdgeRecord Edge, EdgeRecord? Dispatch = null,
        bool HasFrameworkEdge = false);

    private readonly ContextTierContext _context;
    private readonly Dictionary<string, int> _coverage = new(StringComparer.Ordinal);
    private readonly List<(string SymbolId, int Depth, Witness Witness)> _callers = [];

    internal ContextUpstreamWalk(ContextTierContext context)
    {
        _context = context;
        Walk();
    }

    internal List<CapsuleItem> BuildCallers(int minDepth, int maxDepth)
    {
        var items = new List<CapsuleItem>();
        foreach (var (id, depth, witness) in _callers)
        {
            _context.ThrowIfCancellationRequested();
            if (depth > maxDepth)
                break;
            if (depth < minDepth)
                continue;

            var edge = witness.Edge;
            var provenance = edge.Provenance;
            string reason;
            string? relationship = null;
            bool? direct = null;
            if (witness.Dispatch is { } dispatch)
            {
                var grades = witness.HasFrameworkEdge ? new[] { Provenance.FrameworkDerived } : Array.Empty<string>();
                provenance = DispatchClaimProvenance.Compose(grades, dispatch.Provenance);
                reason = $"This dependency uses a {edge.Kind} edge ({edge.Provenance}). "
                         + $"The route to the anchor includes a MayDispatchTo edge ({dispatch.Provenance}). The runtime target can vary.";
                relationship = CapsuleRelationship.IndirectDispatchCandidate;
                direct = false;
            }
            else if (IsFrameworkEntry(edge))
            {
                reason = "Framework route or handler entry point that reaches the anchor.";
            }
            else if (depth == 1)
            {
                reason = $"Direct upstream dependency through a {edge.Kind} edge.";
                relationship = CapsuleRelationship.DirectCaller;
                direct = true;
            }
            else
            {
                reason = "Bounded upstream dependency within the requested hop limit.";
            }

            if (_context.BuildCapsuleItem(id, edge.Kind, provenance, reason, relationship, direct) is { } item)
                items.Add(item);
        }
        return items;
    }

    internal List<CapsuleItem> BuildTests()
    {
        var covered = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, depth) in _coverage)
        {
            _context.ThrowIfCancellationRequested();
            foreach (var candidate in TestSymbolDiscovery.ExpandProductionSymbolIds(id))
                if (!covered.TryGetValue(candidate, out var prior) || depth < prior)
                    covered[candidate] = depth;
        }

        var tests = new Dictionary<string, (int Depth, EdgeRecord Edge)>(StringComparer.Ordinal);
        foreach (var (id, depth) in covered.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            foreach (var edge in _context.GetOutgoingEdges(id))
            {
                _context.ThrowIfCancellationRequested();
                if (edge.Kind == nameof(EdgeKind.TestedBy))
                    tests.TryAdd(edge.TargetSymbolId, (depth, edge));
            }
        }

        var items = new List<CapsuleItem>();
        foreach (var (id, evidence) in tests.OrderBy(pair => pair.Value.Depth).ThenBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var edge = evidence.Edge;
            if (_context.BuildCapsuleItem(id, edge.Kind, edge.Provenance,
                    "Persisted TestedBy evidence connects this test to the change scope.") is { } item)
                items.Add(item);
        }
        return items;
    }

    private void Walk()
    {
        var seeds = _context.EffectiveSymbolIds.ToHashSet(StringComparer.Ordinal);
        var excluded = CallerExclusions(seeds);
        // A dispatch source needs a separate visit so an ordinary route can still reach it.
        var visited = seeds.Select(id => new Visit(id)).ToHashSet();
        IEnumerable<(Visit, Witness?)> level = visited.Select(static visit => (visit, (Witness?)null));
        var frameworkEntries = new Dictionary<string, Witness>(StringComparer.Ordinal);

        foreach (var seed in seeds)
            _coverage.Add(seed, 0);

        for (var depth = 1; depth <= _context.MaxHops; depth++)
        {
            _context.ThrowIfCancellationRequested();
            var next = new Dictionary<Visit, Witness>();
            foreach (var (visit, prior) in level)
            {
                _context.ThrowIfCancellationRequested();
                foreach (var edge in Incoming(visit))
                {
                    var source = edge.SourceSymbolId;
                    var isDispatch = edge.Kind == nameof(EdgeKind.MayDispatchTo);
                    if (isDispatch || IsAccess(edge))
                    {
                        var nextVisit = new Visit(source, isDispatch);
                        if (visited.Contains(nextVisit))
                            continue;

                        var witness = new Witness(visit.SymbolId, edge, isDispatch ? edge : prior?.Dispatch,
                            edge.Provenance == Provenance.FrameworkDerived || prior?.HasFrameworkEdge == true);
                        KeepBest(next, nextVisit, witness);
                    }
                    else if (depth == 1 && IsFrameworkEntry(edge) && !excluded.Contains(source))
                        KeepBest(frameworkEntries, source, new Witness(visit.SymbolId, edge));
                }
            }

            var results = new Dictionary<string, Witness>(StringComparer.Ordinal);
            foreach (var (visit, witness) in next)
            {
                _coverage.TryAdd(visit.SymbolId, depth);
                if (!visit.IsDispatchSource && !excluded.Contains(visit.SymbolId))
                    results.Add(visit.SymbolId, witness);
            }

            if (depth == 1)
            {
                foreach (var (id, witness) in frameworkEntries)
                {
                    results.TryAdd(id, witness);
                    _coverage.TryAdd(id, depth);
                }
            }
            else
            {
                foreach (var id in frameworkEntries.Keys)
                    results.Remove(id);
            }

            foreach (var (id, witness) in results.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                _callers.Add((id, depth, witness));

            if (next.Count == 0)
                break;

            visited.UnionWith(next.Keys);
            level = next.Select(static pair => (pair.Key, (Witness?)pair.Value));
        }
    }

    private HashSet<string> CallerExclusions(HashSet<string> seeds)
    {
        var excluded = new HashSet<string>(seeds, StringComparer.Ordinal);
        if (!_context.SymbolId.IsType)
            return excluded;

        var types = new Queue<string>();
        var visitedTypes = new HashSet<string>(StringComparer.Ordinal) { _context.SymbolId.Value };
        types.Enqueue(_context.SymbolId.Value);
        while (types.TryDequeue(out var id))
        {
            foreach (var edge in _context.GetOutgoingEdges(id))
            {
                _context.ThrowIfCancellationRequested();
                if (edge.Kind == nameof(EdgeKind.Declares))
                    excluded.Add(edge.TargetSymbolId);
                if (edge.Kind == nameof(EdgeKind.Contains) && SymbolId.DocCommentIdOf(edge.TargetSymbolId) is ['T', ':', ..]
                    && visitedTypes.Add(edge.TargetSymbolId))
                {
                    excluded.Add(edge.TargetSymbolId);
                    types.Enqueue(edge.TargetSymbolId);
                }
            }
        }
        return excluded;
    }

    private IEnumerable<EdgeRecord> Incoming(Visit visit)
    {
        var id = visit.SymbolId;
        var metadata = SymbolId.DocCommentIdOf(id) is ['M', ':', ..] ? _context.GetSymbolMetadata(id) : null;
        var role = SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AccessorKind);
        var associatedDocId = SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AssociatedSymbol);
        var assembly = associatedDocId != null ? SymbolId.Parse(id).AssemblyIdentity : null;

        foreach (var edge in IncomingAt(id, visit.IsDispatchSource))
        {
            if (edge.SourceSymbolId != id && !IsSameMember(edge.SourceSymbolId))
                yield return edge;
        }

        if (role == null || associatedDocId == null)
            yield break;

        foreach (var edge in IncomingAt(SymbolId.Compose(associatedDocId, assembly!), visit.IsDispatchSource))
        {
            if (edge.SourceSymbolId != id && IsAccess(edge) && AccessorEdges.Inherits(role, edge.Kind) && !IsSameMember(edge.SourceSymbolId))
                yield return edge;
        }

        bool IsSameMember(string sourceId)
        {
            if (associatedDocId == null || !SymbolId.TryParse(sourceId, out var source) || source.AssemblyIdentity != assembly)
                return false;
            return source.DocCommentId == associatedDocId ||
                   (source.DocCommentId is ['M', ':', ..] &&
                    SymbolMetadata.GetString(_context.GetSymbolMetadata(sourceId), SymbolMetadataKeys.AssociatedSymbol) == associatedDocId);
        }
    }

    private IEnumerable<EdgeRecord> IncomingAt(string targetId, bool isDispatchSource)
    {
        var incoming = _context.GetIncomingEdges(targetId);
        HashSet<string>? markedCallers = null;
        if (isDispatchSource)
        {
            markedCallers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in incoming)
            {
                _context.ThrowIfCancellationRequested();
                if (edge.Kind == nameof(EdgeKind.StaticallyCalls))
                    markedCallers.Add(edge.SourceSymbolId);
            }
        }

        foreach (var edge in incoming)
        {
            _context.ThrowIfCancellationRequested();
            if (markedCallers == null || !IsAccess(edge) || edge.Kind == nameof(EdgeKind.MethodGroupRef) ||
                markedCallers.Contains(edge.SourceSymbolId))
                yield return edge;
        }
    }

    private static bool IsAccess(EdgeRecord edge) =>
        edge.Kind is nameof(EdgeKind.Calls) or nameof(EdgeKind.MethodGroupRef) or nameof(EdgeKind.Reads) or nameof(EdgeKind.Writes);

    private static bool IsFrameworkEntry(EdgeRecord edge) =>
        edge.Kind is nameof(EdgeKind.RoutesTo) or nameof(EdgeKind.Handles);

    private static void KeepBest<TKey>(Dictionary<TKey, Witness> visits, TKey key, Witness witness) where TKey : notnull
    {
        if (!visits.TryGetValue(key, out var prior) || Compare(witness, prior) < 0)
            visits[key] = witness;
    }

    private static int Compare(Witness left, Witness right)
    {
        var order = (left.Dispatch != null).CompareTo(right.Dispatch != null);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.ParentId, right.ParentId);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Edge.Kind, right.Edge.Kind);
        if (order != 0) return order;
        order = StringComparer.Ordinal.Compare(left.Edge.TargetSymbolId, right.Edge.TargetSymbolId);
        if (order != 0) return order;
        return StringComparer.Ordinal.Compare(left.Dispatch?.TargetSymbolId, right.Dispatch?.TargetSymbolId);
    }
}
