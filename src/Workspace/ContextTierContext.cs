using Lurp.Storage;

namespace Lurp.Workspace;

internal sealed class ContextTierContext(IEdgeStore edgeStore, IDeclarationStore declarationStore, string snapshotId, SymbolId symbolId, int maxHops, bool includeGenerated)
{
    private readonly Dictionary<(string SymbolId, bool IncludeGenerated), DeclarationLocation?> _locationCache = [];

    internal IEdgeStore EdgeStore { get; } = edgeStore;
    internal IDeclarationStore DeclarationStore { get; } = declarationStore;
    internal string SnapshotId { get; } = snapshotId;
    internal SymbolId SymbolId { get; } = symbolId;
    internal int MaxHops { get; } = maxHops;
    internal bool IncludeGenerated { get; } = includeGenerated;

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
