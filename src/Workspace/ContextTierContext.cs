using Lurp.Storage;

namespace Lurp.Workspace;

internal sealed class ContextTierContext(IEdgeStore edgeStore, IDeclarationStore declarationStore, string snapshotId, SymbolId symbolId, int maxHops, bool includeGenerated)
{
    private readonly Dictionary<(string SymbolId, bool IncludeGenerated), DeclarationLocation?> _locationCache = [];
    private readonly Dictionary<(string CandidateTypeId, string ReceiverTypeId), bool> _assignabilityCache = [];
    private IReadOnlyList<string>? _effectiveSymbolIds;
    private bool? _hasUnmodeledRegistrations;

    internal IEdgeStore EdgeStore { get; } = edgeStore;
    internal IDeclarationStore DeclarationStore { get; } = declarationStore;
    internal string SnapshotId { get; } = snapshotId;
    internal SymbolId SymbolId { get; } = symbolId;
    internal int MaxHops { get; } = maxHops;
    internal bool IncludeGenerated { get; } = includeGenerated;

    // Member-level facts (Calls, Constructs, MayDispatchTo, TestedBy) are
    // persisted on member symbol IDs; a type anchor carries none of them. Tier
    // builders therefore query an effective scope: the anchor id plus, for a
    // type anchor, the ids of its directly declared members resolved through the
    // persisted Declares edges. Each id is queried independently and results
    // keep the originating edge's kind and provenance, so no type-level edges
    // are synthesized and member multiplicity is preserved.
    internal IReadOnlyList<string> EffectiveSymbolIds => _effectiveSymbolIds ??= ComputeEffectiveSymbolIds();

    internal List<EdgeRecord> GetMayDispatchEdges(string symbolId)
        => EdgeStore.GetOutgoingEdges(SnapshotId, symbolId)
            .Where(edge => edge.Kind == EdgeKind.MayDispatchTo.ToString())
            .ToList();

    internal List<string> GetDeclaringTypeIds(string memberSymbolId)
        => EdgeStore.GetIncomingEdges(SnapshotId, memberSymbolId)
            .Where(edge => edge.Kind == EdgeKind.Declares.ToString())
            .Select(edge => edge.SourceSymbolId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    internal bool IsReceiverCompatible(string candidateTypeId, IReadOnlyList<IReadOnlyList<string>> receiverAlternatives)
        => receiverAlternatives.Any(requiredTypes =>
            requiredTypes.Count > 0 && requiredTypes.All(receiverTypeId => IsAssignableTo(candidateTypeId, receiverTypeId)));

    private bool IsAssignableTo(string candidateTypeId, string receiverTypeId)
    {
        var cacheKey = (candidateTypeId, receiverTypeId);
        if (_assignabilityCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var visited = new HashSet<string>(StringComparer.Ordinal) { candidateTypeId };
        var queue = new Queue<string>();
        queue.Enqueue(candidateTypeId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (string.Equals(current, receiverTypeId, StringComparison.Ordinal))
            {
                _assignabilityCache[cacheKey] = true;
                return true;
            }

            foreach (var edge in EdgeStore.GetOutgoingEdges(SnapshotId, current))
            {
                if (edge.Kind is not (nameof(EdgeKind.Implements) or nameof(EdgeKind.Inherits)))
                    continue;
                if (visited.Add(edge.TargetSymbolId))
                    queue.Enqueue(edge.TargetSymbolId);
            }
        }

        _assignabilityCache[cacheKey] = false;
        return false;
    }

    // For a given concrete symbol, returns the interface/abstract members that
    // dispatch TO it. An incoming MayDispatchTo edge <source → this symbol>
    // means "source dispatches to this concrete implementation at runtime".
    // Tier builders chain this with Calls-upstream traversal to find callers
    // that reach the anchor through interface dispatch.
    internal List<EdgeRecord> GetDispatchSourceEdges(string symbolId)
        => EdgeStore.GetIncomingEdges(SnapshotId, symbolId)
            .Where(edge => edge.Kind == EdgeKind.MayDispatchTo.ToString())
            .ToList();

    internal bool HasUnmodeledRegistrations()
    {
        if (_hasUnmodeledRegistrations.HasValue)
            return _hasUnmodeledRegistrations.Value;

        // Registration is a type-level fact, so a member anchor must also
        // consider its declaring type; otherwise an unmodeled registration on
        // the type lets a member anchor's tier report "empty" (the exact false
        // negative this method exists to prevent).
        var scope = EffectiveSymbolIds
            .Concat(EffectiveSymbolIds.SelectMany(GetDeclaringTypeIds))
            .Distinct(StringComparer.Ordinal);

        foreach (var symbolId in scope)
        {
            var incoming = EdgeStore.GetIncomingEdges(SnapshotId, symbolId);
            foreach (var edge in incoming)
            {
                var sourceOutgoing = EdgeStore.GetOutgoingEdges(SnapshotId, edge.SourceSymbolId);
                if (sourceOutgoing.Any(e => e.Kind == EdgeKind.Registers.ToString() && e.TargetSymbolId == GraphNodeIds.RuntimeUnknown))
                {
                    _hasUnmodeledRegistrations = true;
                    return true;
                }
            }

            var outgoing = EdgeStore.GetOutgoingEdges(SnapshotId, symbolId);
            if (outgoing.Any(e => e.Kind == EdgeKind.Registers.ToString() && e.TargetSymbolId == GraphNodeIds.RuntimeUnknown))
            {
                _hasUnmodeledRegistrations = true;
                return true;
            }
        }

        _hasUnmodeledRegistrations = false;
        return false;
    }

    private IReadOnlyList<string> ComputeEffectiveSymbolIds()
    {
        var ids = new List<string> { SymbolId.Value };
        if (SymbolId.DocCommentId.Length >= 2 && SymbolId.DocCommentId[0] == 'T' && SymbolId.DocCommentId[1] == ':')
        {
            foreach (var edge in EdgeStore.GetOutgoingEdges(SnapshotId, SymbolId.Value))
            {
                if (edge.Kind == EdgeKind.Declares.ToString())
                    ids.Add(edge.TargetSymbolId);
            }
        }
        return ids;
    }

    internal CapsuleItem? BuildCapsuleItem(string symbolId, string edgeKind, string provenance, string? inclusionReason = null,
        string? relationship = null, bool? direct = null)
    {
        var info = DeclarationStore.GetSymbolInfo(symbolId, SnapshotId);
        if (info == null)
            return null;

        var source = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, IncludeGenerated);

        if (!IncludeGenerated && source == null)
        {
            var hasGeneratedOnly = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, true) != null;
            if (hasGeneratedOnly)
                return null;
        }

        var location = GetDeclarationLocation(symbolId);
        return new CapsuleItem(symbolId: symbolId, kind: info.Kind.ToString(),
            fullyQualifiedName: info.FullyQualifiedName ?? symbolId,
            provenance: provenance,
            edgeKind: edgeKind,
            source: source,
            location: location,
            inclusionReason: inclusionReason,
            relationship: relationship,
            direct: direct);
    }

    private DeclarationLocation? GetDeclarationLocation(string symbolId)
    {
        // The same symbol recurs across tiers (direct callers, second-degree
        // context, registered implementations, change sites); memoize per
        // symbol so the declaration-location lookup is not repeated per item.
        var key = (symbolId, IncludeGenerated);
        if (!_locationCache.TryGetValue(key, out var location))
        {
            location = DeclarationStore.GetDeclarationLocations(symbolId, SnapshotId, IncludeGenerated).FirstOrDefault();
            _locationCache[key] = location;
        }
        return location;
    }
}
