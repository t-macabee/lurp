using Lurp.Storage;

namespace Lurp.Workspace;

internal sealed class ContextTierContext(IEdgeStore edgeStore, IDeclarationStore declarationStore, string snapshotId, SymbolId symbolId, int maxHops, bool includeGenerated)
{
    private readonly Dictionary<(string SymbolId, bool IncludeGenerated), DeclarationLocation?> _locationCache = [];
    private IReadOnlyList<string>? _effectiveSymbolIds;

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

    internal CapsuleItem? BuildCapsuleItem(string symbolId, string edgeKind, string provenance, string? inclusionReason = null)
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
            inclusionReason: inclusionReason);
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
