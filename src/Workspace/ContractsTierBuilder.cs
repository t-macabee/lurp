using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
///     Surfaces the anchor's contracts: base types, implemented interfaces, and
///     overridden members. Member-level contract edges (Overrides) are reached
///     through the effective anchor scope, so a type anchor surfaces the same
///     contracts its members carry. Targets declared outside the snapshot (e.g.
///     framework base types from referenced assemblies) are surfaced as items
///     derived from the persisted edge instead of being dropped, so an external
///     contract is never reported as absent; the inclusion reason marks them as
///     external.
/// </summary>
internal sealed class ContractsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    private static readonly HashSet<string> _allowedKinds =
    [
        nameof(EdgeKind.Inherits),
        nameof(EdgeKind.Implements),
        nameof(EdgeKind.Overrides)
    ];

    string IContextTierBuilder.Name => "contracts";

    string IContextTierBuilder.InclusionReason => "Compiler-resolved contracts implemented or overridden by the anchor.";

    TierBindingScope IContextTierBuilder.BindingScope => TierBindingScope.AnchorDocuments;

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            context.ThrowIfCancellationRequested();
            foreach (var edge in context.GetOutgoingEdges(symbolId))
            {
                if (!_allowedKinds.Contains(edge.Kind) || edge.TargetSymbolId == symbolId)
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

                // Only a target outside the snapshot gets an external item.
                if (context.GetSymbolInfo(edge.TargetSymbolId) != null)
                    continue;

                var external = BuildExternalItem(edge);
                if (external != null)
                    results.Add(external);
            }

            foreach (var edge in context.GetDispatchSourceEdges(symbolId))
            {
                context.ThrowIfCancellationRequested();
                if (!context.IsProvedInterfaceDispatch(edge) || !seen.Add(edge.SourceSymbolId))
                    continue;

                if (context.BuildCapsuleItem(edge.SourceSymbolId, edge.Kind, edge.Provenance,
                        "Interface member implemented by the anchor.") is { } item)
                    results.Add(item);
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
            edge.TargetSymbolId,
            kind,
            symbolId.DocCommentId[(symbolId.DocCommentId.IndexOf(':') + 1)..],
            edge.Provenance,
            edge.Kind,
            null,
            null,
            "External contract: base type, interface, or overridden member declared outside this snapshot.");
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
            _ => null
        };
    }
}