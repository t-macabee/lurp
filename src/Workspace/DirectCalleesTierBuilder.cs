using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCalleesTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "directCallees";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();
        var allowedKinds = new HashSet<string>
        {
            EdgeKind.Calls.ToString(),
            EdgeKind.Constructs.ToString()
        };

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            foreach (var edge in context.EdgeStore.GetOutgoingEdges(context.SnapshotId, symbolId))
            {
                if (!allowedKinds.Contains(edge.Kind))
                    continue;

                AddItem(edge.TargetSymbolId, edge.Kind, edge.Provenance,
                    "Direct call or construction target of the anchor.");

                if (edge.Kind == EdgeKind.Calls.ToString())
                    AddMayDispatchTargets(edge);
            }
        }

        return results;

        void AddItem(string symbolId, string edgeKind, string provenance, string inclusionReason)
        {
            if (!seen.Add(symbolId))
                return;

            var item = context.BuildCapsuleItem(symbolId, edgeKind, provenance, inclusionReason);
            if (item != null)
            {
                results.Add(item);
            }
        }

        void AddMayDispatchTargets(Lurp.Storage.EdgeRecord callEdge)
        {
            var receiverAlternatives = ReceiverTypeConstraints.Deserialize(callEdge.ReceiverTypeConstraintsJson)
                .Select(static alternative => (IReadOnlyList<string>)alternative)
                .ToList();
            if (receiverAlternatives.Count == 0)
                return;

            foreach (var edge in context.GetMayDispatchEdges(callEdge.TargetSymbolId))
            {
                var declaringTypes = context.GetDeclaringTypeIds(edge.TargetSymbolId);
                if (!declaringTypes.Any(candidateTypeId =>
                        context.IsReceiverCompatible(candidateTypeId, receiverAlternatives)))
                {
                    continue;
                }

                AddItem(edge.TargetSymbolId, edge.Kind, Provenance.CompilerProved,
                    "Implementation of a called interface or virtual member whose containing type is " +
                    "assignable to the call site's persisted static receiver-type constraints. This proves " +
                    "receiver compatibility, not the runtime target.");
            }
        }
    }
}
