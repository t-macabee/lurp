using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
/// Surfaces the anchor's contracts: base types, implemented interfaces, and
/// overridden members. Member-level contract edges (Overrides) are reached
/// through the effective anchor scope, so a type anchor surfaces the same
/// contracts its members carry. Targets declared outside the snapshot (e.g.
/// framework base types from referenced assemblies) are surfaced as items
/// derived from the persisted edge instead of being dropped, so an external
/// contract is never reported as absent; the inclusion reason marks them as
/// external.
/// </summary>
internal sealed class ContractsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "contracts";

    string IContextTierBuilder.InclusionReason => "Compiler-resolved contracts implemented or overridden by the anchor.";

    private static readonly HashSet<string> _allowedKinds =
    [
        EdgeKind.Inherits.ToString(),
        EdgeKind.Implements.ToString(),
        EdgeKind.Overrides.ToString(),
    ];

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            foreach (var edge in context.EdgeStore.GetOutgoingEdges(context.SnapshotId, symbolId))
            {
                if (!_allowedKinds.Contains(edge.Kind))
                    continue;
                if (!seen.Add(edge.TargetSymbolId))
                    continue;

                var item = context.BuildCapsuleItem(edge.TargetSymbolId, edge.Kind, edge.Provenance,
                    "Contract directly inherited, implemented, or overridden by the anchor.");
                if (item != null)
                {
                    results.Add(item);
                    continue;
                }

                // The target is external to this snapshot (e.g. a framework base
                // type from a referenced assembly). The edge is a persisted fact;
                // surface it under its canonical symbol name instead of dropping
                // it silently, so an external contract is never reported as
                // absent. The edge's provenance is preserved.
                var external = BuildExternalItem(edge);
                if (external != null)
                    results.Add(external);
            }
        }

        return results;
    }

    private static CapsuleItem? BuildExternalItem(EdgeRecord edge)
    {
        var symbolId = SymbolId.Parse(edge.TargetSymbolId);
        var kind = ExternalSymbolKind(symbolId.DocCommentId);
        if (kind == null)
            return null;

        return new CapsuleItem(
            symbolId: edge.TargetSymbolId,
            kind: kind,
            fullyQualifiedName: symbolId.DocCommentId[(symbolId.DocCommentId.IndexOf(':') + 1)..],
            provenance: edge.Provenance,
            edgeKind: edge.Kind,
            source: null,
            location: null,
            inclusionReason: "External contract: base type, interface, or overridden member declared outside this snapshot.");
    }

    private static string? ExternalSymbolKind(string docCommentId)
    {
        if (docCommentId.Length < 2 || docCommentId[1] != ':')
            return null;

        return docCommentId[0] switch
        {
            'T' => nameof(IndexedSymbolKind.Type),
            'M' => nameof(IndexedSymbolKind.Method),
            'P' => nameof(IndexedSymbolKind.Property),
            'F' => nameof(IndexedSymbolKind.Field),
            'E' => nameof(IndexedSymbolKind.Event),
            'N' => nameof(IndexedSymbolKind.Namespace),
            _ => null,
        };
    }
}
