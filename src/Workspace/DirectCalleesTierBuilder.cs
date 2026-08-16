using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCalleesTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "direct_callees";

    string IContextTierBuilder.InclusionReason => "Direct compiler-resolved calls or constructions made by the anchor.";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();
        var allowedKinds = new HashSet<string>
        {
            nameof(EdgeKind.Calls),
            nameof(EdgeKind.Constructs)
        };

        foreach (var symbolId in context.EffectiveSymbolIds)
            foreach (var edge in context.EdgeStore.GetOutgoingEdges(context.SnapshotId, symbolId))
            {
                if (!allowedKinds.Contains(edge.Kind))
                    continue;

                // Direct callees are registered first so a dispatch projection
                // can never replace a genuine direct call's stronger label.
                AddItem(edge.TargetSymbolId, edge.Kind, edge.Provenance,
                    "Direct call or construction target of the anchor.",
                    CapsuleRelationship.DirectCallee, true);
            }

        // Dispatch projections run after the direct pass and only for targets
        // not already present as direct callees (the shared seen set skips them).
        foreach (var symbolId in context.EffectiveSymbolIds)
            foreach (var edge in context.EdgeStore.GetOutgoingEdges(context.SnapshotId, symbolId).Where(edge => edge.Kind == nameof(EdgeKind.Calls)))
                AddMayDispatchTargets(edge);

        return results;

        void AddItem(string symbolId, string edgeKind, string provenance, string inclusionReason,
            string? relationship = null, bool? direct = null)
        {
            if (!seen.Add(symbolId))
                return;

            var item = context.BuildCapsuleItem(symbolId, edgeKind, provenance, inclusionReason,
                relationship, direct);
            if (item != null) results.Add(item);
        }

        void AddMayDispatchTargets(EdgeRecord callEdge)
        {
            var receiverAlternatives = ReceiverTypeConstraints.Deserialize(callEdge.ReceiverTypeConstraintsJson)
                .Select(static alternative => alternative.AsReadOnly())
                .ToList();
            if (receiverAlternatives.Count == 0)
                return;

            foreach (var edge in context.GetMayDispatchEdges(callEdge.TargetSymbolId))
            {
                var declaringTypes = context.GetDeclaringTypeIds(edge.TargetSymbolId);
                if (!declaringTypes.Any(candidateTypeId =>
                        context.IsReceiverCompatible(candidateTypeId, receiverAlternatives)))
                    continue;

                // The call targets the interface/abstract member, not this
                // implementation. The candidate is included only because it
                // globally implements the called member (the MayDispatchTo
                // relation, structurally compiler-proved in the graph);
                // receiver-type compatibility narrows which global
                // implementations are candidates but does not prove this call
                // selects it at runtime. The projected item is a
                // global_implementation_relation, never a direct callee.
                AddItem(edge.TargetSymbolId, edge.Kind, Provenance.GlobalImplementationRelation,
                    "Dispatch-projected implementation of a called interface or virtual member: the anchor " +
                    $"calls the interface/abstract member via a Calls edge ({callEdge.Provenance}), which may " +
                    $"dispatch to this implementation at runtime via a MayDispatchTo edge ({edge.Provenance}). " +
                    "The containing type is assignable to the call site's persisted static receiver-type " +
                    "constraints, which narrows but does not establish the runtime target, so this is not " +
                    "a direct callee.",
                    CapsuleRelationship.IndirectDispatchCandidate, false);
            }
        }
    }
}