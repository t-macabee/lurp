using System.Text.Json;

namespace Lurp.Workspace;

internal sealed class ContextTierContext(IEdgeStore edgeStore, IDeclarationStore declarationStore, string snapshotId, SymbolId symbolId, int maxHops, bool includeGenerated, CancellationToken cancellationToken = default)
{
    private readonly Dictionary<(string CandidateTypeId, string ReceiverTypeId), bool> _assignabilityCache = [];
    private readonly Dictionary<(string SymbolId, bool IncludeGenerated), DeclarationLocation?> _locationCache = [];
    private readonly Dictionary<string, List<EdgeRecord>> _incoming = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EdgeRecord>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IndexedSymbolInfo?> _symbols = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement?> _metadata = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string SymbolId, string AccessorKind)>> _accessors = new(StringComparer.Ordinal);
    private bool? _hasUnmodeledRegistrations;

    internal IEdgeStore EdgeStore { get; } = edgeStore;
    internal IDeclarationStore DeclarationStore { get; } = declarationStore;
    internal string SnapshotId { get; } = snapshotId;
    internal SymbolId SymbolId { get; } = symbolId;
    internal int MaxHops { get; } = maxHops;
    internal bool IncludeGenerated { get; } = includeGenerated;
    internal CancellationToken CancellationToken { get; } = cancellationToken;

    // Tier builders call this at each loop that reads the store, so a cancelled
    // request stops inside the assembly. The context is per request and never
    // shared, so holding the token here is safe.
    internal void ThrowIfCancellationRequested() => CancellationToken.ThrowIfCancellationRequested();

    // Member-level facts (Calls, Constructs, MayDispatchTo, TestedBy) are
    // persisted on member symbol IDs; a type anchor carries none of them. Tier
    // builders therefore query an effective scope: the anchor id plus, for a
    // type anchor, its directly declared members, or for a property or event
    // anchor, its accessors. Each id is queried independently and results
    // keep the originating edge's kind and provenance, so no type-level edges
    // are synthesized and member multiplicity is preserved.
    internal IReadOnlyList<string> EffectiveSymbolIds => field ??= ComputeEffectiveSymbolIds();

    internal ContextUpstreamWalk Upstream => field ??= new ContextUpstreamWalk(this);

    internal List<EdgeRecord> GetIncomingEdges(string id)
    {
        ThrowIfCancellationRequested();
        if (!_incoming.TryGetValue(id, out var edges))
            _incoming[id] = edges = EdgeStore.GetIncomingEdges(SnapshotId, id);
        return edges;
    }

    internal List<EdgeRecord> GetOutgoingEdges(string id)
    {
        ThrowIfCancellationRequested();
        if (!_outgoing.TryGetValue(id, out var edges))
            _outgoing[id] = edges = EdgeStore.GetOutgoingEdges(SnapshotId, id);
        return edges;
    }

    internal IndexedSymbolInfo? GetSymbolInfo(string id)
    {
        ThrowIfCancellationRequested();
        if (!_symbols.TryGetValue(id, out var info))
            _symbols[id] = info = DeclarationStore.GetSymbolInfo(id, SnapshotId);
        return info;
    }

    internal JsonElement? GetSymbolMetadata(string id)
    {
        if (!_metadata.TryGetValue(id, out var metadata))
            _metadata[id] = metadata = SymbolMetadata.Parse(GetSymbolInfo(id)?.MetadataJson, id);
        return metadata;
    }

    internal List<(string SymbolId, string AccessorKind)> GetAccessors(string id)
    {
        ThrowIfCancellationRequested();
        if (!_accessors.TryGetValue(id, out var accessors))
            _accessors[id] = accessors = DeclarationStore.GetAccessorsForSymbol(SnapshotId, id);
        return accessors;
    }

    internal List<EdgeRecord> GetMayDispatchEdges(string symbolId)
    {
        return [.. GetOutgoingEdges(symbolId)
            .Where(edge => edge.Kind == nameof(EdgeKind.MayDispatchTo))];
    }

    internal List<string> GetDeclaringTypeIds(string memberSymbolId)
    {
        return [.. GetIncomingEdges(memberSymbolId)
            .Where(edge => edge.Kind == nameof(EdgeKind.Declares))
            .Select(edge => edge.SourceSymbolId)
            .Distinct(StringComparer.Ordinal)];
    }

    internal bool IsReceiverCompatible(string candidateTypeId, IReadOnlyList<IReadOnlyList<string>> receiverAlternatives)
    {
        return receiverAlternatives.Any(requiredTypes =>
            requiredTypes.Count > 0 && requiredTypes.All(receiverTypeId => IsAssignableTo(candidateTypeId, receiverTypeId)));
    }

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

            foreach (var edge in GetOutgoingEdges(current))
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

    internal List<EdgeRecord> GetDispatchSourceEdges(string symbolId)
    {
        return [.. GetIncomingEdges(symbolId)
            .Where(edge => edge.Kind == nameof(EdgeKind.MayDispatchTo))];
    }

    internal bool IsProvedInterfaceDispatch(EdgeRecord edge)
    {
        if (edge.Kind != nameof(EdgeKind.MayDispatchTo) || edge.Provenance != Provenance.CompilerProved ||
            edge.SourceSymbolId == edge.TargetSymbolId ||
            SymbolId.DocCommentIdOf(edge.SourceSymbolId) is not [('M' or 'P' or 'E'), ':', ..])
            return false;

        return GetDeclaringTypeIds(edge.SourceSymbolId).Any(owner =>
            SymbolId.DocCommentIdOf(owner) is ['T', ':', ..] &&
            SymbolMetadata.GetString(GetSymbolMetadata(owner), SymbolMetadataKeys.TypeKind) == "Interface");
    }

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
            var incoming = GetIncomingEdges(symbolId);
            foreach (var edge in incoming)
            {
                var sourceOutgoing = GetOutgoingEdges(edge.SourceSymbolId);
                if (sourceOutgoing.Any(e => e.Kind == nameof(EdgeKind.Registers) && e.TargetSymbolId == GraphNodeIds.RuntimeUnknown))
                {
                    _hasUnmodeledRegistrations = true;
                    return true;
                }
            }

            var outgoing = GetOutgoingEdges(symbolId);
            if (outgoing.Any(e => e.Kind == nameof(EdgeKind.Registers) && e.TargetSymbolId == GraphNodeIds.RuntimeUnknown))
            {
                _hasUnmodeledRegistrations = true;
                return true;
            }
        }

        _hasUnmodeledRegistrations = false;
        return false;
    }

    private List<string> ComputeEffectiveSymbolIds()
    {
        var ids = new List<string> { SymbolId.Value };
        if (SymbolId.IsType)
            ids.AddRange(GetOutgoingEdges(SymbolId.Value)
                .Where(edge => edge.Kind == nameof(EdgeKind.Declares))
                .Select(edge => edge.TargetSymbolId));
        else if (SymbolId.DocCommentId is [('P' or 'E'), ':', ..])
            ids.AddRange(GetAccessors(SymbolId.Value).Select(accessor => accessor.SymbolId));
        return ids;
    }

    internal CapsuleItem? BuildCapsuleItem(string symbolId, string edgeKind, string provenance, string? inclusionReason = null,
        string? relationship = null, bool? direct = null)
    {
        ThrowIfCancellationRequested();
        var info = GetSymbolInfo(symbolId);
        if (info == null)
            return symbolId.StartsWith(GraphNodeIds.RoutePrefix, StringComparison.Ordinal)
                ? new CapsuleItem(symbolId, "Route", symbolId, provenance, edgeKind,
                    inclusionReason: inclusionReason, relationship: relationship, direct: direct)
                : null;

        var source = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, IncludeGenerated);

        if (!IncludeGenerated && source == null)
        {
            var hasGeneratedOnly = DeclarationStore.GetSymbolSource(symbolId, SnapshotId, ViewKind.Declaration, true) != null;
            if (hasGeneratedOnly)
                return null;
        }

        var location = GetDeclarationLocation(symbolId);
        return new CapsuleItem(symbolId, info.Kind.ToString(),
            info.FullyQualifiedName ?? symbolId,
            provenance,
            edgeKind,
            source,
            location,
            inclusionReason,
            relationship,
            direct);
    }

    private DeclarationLocation? GetDeclarationLocation(string symbolId)
    {
        // The same symbol recurs across tiers (direct callers, second-degree
        // context, registered implementations, change sites); memoize per
        // symbol so the declaration-location lookup is not repeated per item.
        var key = (symbolId, IncludeGenerated);
        if (_locationCache.TryGetValue(key, out var location))
            return location;

        location = DeclarationStore.GetDeclarationLocations(symbolId, SnapshotId, IncludeGenerated).FirstOrDefault();
        _locationCache[key] = location;
        return location;
    }
}
