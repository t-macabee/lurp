using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCallersTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "direct_callers";

    string IContextTierBuilder.InclusionReason => "Direct callers and framework entry points that can reach the anchor.";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();
        var allowedKinds = new HashSet<string>
        {
            nameof(EdgeKind.Calls)
        };
        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);

        const string directReason =
            "Direct caller that can be affected by changing the anchor.";

        foreach (var symbolId in context.EffectiveSymbolIds)
            AddCallersOf(symbolId, directReason);

        // Callers reached through interface/abstract dispatch (Calls +
        // MayDispatchTo) are classified separately: they are indirect dispatch
        // candidates, never direct compiler-proved callers of the anchor.
        foreach (var symbolId in context.EffectiveSymbolIds)
            foreach (var dispatchEdge in context.GetDispatchSourceEdges(symbolId))
                AddCallersOf(dispatchEdge.SourceSymbolId, null, dispatchEdge.Provenance);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, symbolId);
            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != nameof(EdgeKind.RoutesTo) &&
                    edge.Kind != nameof(EdgeKind.Handles))
                    continue;

                var sourceId = edge.SourceSymbolId;
                if (!seen.Add(sourceId))
                    continue;

                var item = context.BuildCapsuleItem(sourceId, edge.Kind, edge.Provenance,
                    "Framework route or handler entry point that reaches the anchor.");
                if (item != null) results.Add(item);
            }
        }

        return results;

        void AddCallersOf(string targetSymbolId, string? inclusionReason, string? dispatchProvenance = null)
        {
            var paths = traverser.TraceImpact(targetSymbolId, ImpactDirection.Upstream, allowedKinds, maxDepth: 1);
            foreach (var path in paths)
                foreach (var hop in path.Hops)
                {
                    var callerId = hop.SourceSymbolId;
                    if (!seen.Add(callerId))
                        continue;

                    if (dispatchProvenance == null)
                    {
                        // A genuine source-level call directly targeting the
                        // anchor stays a direct caller with the hop's own
                        // provenance (compiler_proved for a real call).
                        var directItem = context.BuildCapsuleItem(callerId, hop.EdgeKind, hop.Provenance,
                            inclusionReason, CapsuleRelationship.DirectCaller, true);
                        if (directItem != null) results.Add(directItem);
                        continue;
                    }

                    // The caller reaches the anchor through interface/abstract
                    // dispatch. The composed claim is possible : the compiler
                    // proves the structural edges, not the runtime dispatch
                    // target : and the item is presented as an indirect
                    // dispatch candidate, never as a direct caller.
                    var provenance = EdgeDedup.ComposeDispatchClaimProvenance(
                        [hop.Provenance], dispatchProvenance);
                    var item = context.BuildCapsuleItem(callerId, hop.EdgeKind, provenance,
                        BuildDispatchReason(dispatchProvenance, hop.Provenance),
                        CapsuleRelationship.IndirectDispatchCandidate, false);
                    if (item != null) results.Add(item);
                }
        }
    }

    // Both underlying steps stay visible: the caller's direct Calls edge to the
    // interface/abstract member, and the MayDispatchTo edge carrying the
    // structural implementation candidate.
    private static string BuildDispatchReason(string dispatchProvenance, string callProvenance)
    {
        return "Indirect dispatch candidate: this caller directly calls the interface/abstract "
               + $"member via a Calls edge ({callProvenance}), which may dispatch to this "
               + $"implementation at runtime via a MayDispatchTo edge ({dispatchProvenance}). The "
               + "compiler establishes the structural implementation, not the runtime dispatch "
               + "target, so this caller is not a direct compiler-proved caller of the anchor.";
    }
}