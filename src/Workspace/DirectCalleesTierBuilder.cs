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

        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            var paths = traverser.TraceImpact(symbolId, ImpactDirection.Downstream, allowedKinds, maxDepth: 1);
            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                {
                    AddItem(hop.TargetSymbolId, hop.EdgeKind, hop.Provenance,
                        "Direct call or construction target of the anchor.");

                    // Interface dispatch rule: a Calls hop that lands on an
                    // interface/abstract member surfaces that member's persisted
                    // MayDispatchTo implementations. Each target keeps the
                    // MayDispatchTo edge's own kind and provenance.
                    if (hop.EdgeKind == EdgeKind.Calls.ToString())
                        AddMayDispatchTargets(hop.TargetSymbolId);
                }
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

        void AddMayDispatchTargets(string calledSymbolId)
        {
            foreach (var edge in context.GetMayDispatchEdges(calledSymbolId))
            {
                AddItem(edge.TargetSymbolId, edge.Kind, edge.Provenance,
                    "Persisted MayDispatchTo implementation of a called interface member.");
            }
        }
    }
}
