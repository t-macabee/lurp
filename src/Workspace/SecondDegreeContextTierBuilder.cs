using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SecondDegreeContextTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "second_degree_context";

    string IContextTierBuilder.InclusionReason => "Bounded upstream paths within the requested hop limit.";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var allowedKinds = new HashSet<string>
        {
            nameof(EdgeKind.Calls)
        };

        if (context.MaxHops <= 1)
            return results;

        var effectiveSymbolIds = context.EffectiveSymbolIds;
        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);

        var seen = new HashSet<string>();

        const string directReason =
            "Bounded upstream dependency within the requested hop limit.";

        foreach (var symbolId in effectiveSymbolIds)
            AddUpstreamNeighbors(symbolId, directReason);

        // Upstream dependencies reached through interface/abstract dispatch
        // (Calls + MayDispatchTo) are classified separately: they are indirect
        // dispatch candidates, never direct compiler-proved dependencies.
        foreach (var symbolId in effectiveSymbolIds)
            foreach (var dispatchEdge in context.GetDispatchSourceEdges(symbolId))
                AddUpstreamNeighbors(dispatchEdge.SourceSymbolId, null, dispatchEdge.Provenance);

        return results;

        void AddUpstreamNeighbors(string symbolId, string? inclusionReason, string? dispatchProvenance = null)
        {
            var paths = traverser.TraceImpact(symbolId, ImpactDirection.Upstream, allowedKinds, maxDepth: context.MaxHops);
            foreach (var path in paths)
                foreach (var hop in path.Hops)
                {
                    var neighborId = hop.SourceSymbolId;
                    if (!seen.Add(neighborId))
                        continue;

                    if (effectiveSymbolIds.Contains(neighborId))
                        continue;

                    if (dispatchProvenance == null)
                    {
                        var directItem = context.BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance,
                            inclusionReason);
                        if (directItem != null) results.Add(directItem);
                        continue;
                    }

                    // The neighbor reaches the anchor through interface/abstract
                    // dispatch. The composed claim is possible : the compiler
                    // proves the structural edges, not the runtime dispatch
                    // target : unless a framework edge participates anywhere in
                    // the path. The item is never a direct compiler-proved
                    // dependency of the anchor.
                    var provenance = EdgeDedup.ComposeDispatchClaimProvenance(
                        path.Hops.Select(static hop => hop.Provenance), dispatchProvenance);
                    var item = context.BuildCapsuleItem(neighborId, hop.EdgeKind, provenance,
                        BuildDispatchReason(dispatchProvenance, hop.Provenance),
                        CapsuleRelationship.IndirectDispatchCandidate, false);
                    if (item != null) results.Add(item);
                }
        }
    }

    // Both underlying steps stay visible: the Calls edge to the interface/
    // abstract member, and the MayDispatchTo edge carrying the structural
    // implementation candidate.
    private static string BuildDispatchReason(string dispatchProvenance, string callProvenance)
    {
        return "Upstream dependency reached through interface dispatch: it calls the "
               + $"interface/abstract member via a Calls edge ({callProvenance}), which may "
               + "dispatch to this implementation at runtime via a MayDispatchTo edge "
               + $"({dispatchProvenance}). The runtime dispatch target is not compiler-established, "
               + "so this is not a direct compiler-proved dependency of the anchor.";
    }
}