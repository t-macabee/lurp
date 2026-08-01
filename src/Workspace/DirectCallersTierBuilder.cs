using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DirectCallersTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "directCallers";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();
        var allowedKinds = new HashSet<string>
        {
            EdgeKind.Calls.ToString()
        };

        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            var paths = traverser.TraceImpact(symbolId, ImpactDirection.Upstream, allowedKinds, maxDepth: 1);
            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                {
                    var callerId = hop.SourceSymbolId;
                    if (!seen.Add(callerId))
                        continue;

                    var item = context.BuildCapsuleItem(callerId, hop.EdgeKind, hop.Provenance,
                        "Direct caller that can be affected by changing the anchor.");
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }
        }

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, symbolId);
            foreach (var edge in incomingEdges)
            {
                if (edge.Kind != EdgeKind.RoutesTo.ToString() &&
                    edge.Kind != EdgeKind.Handles.ToString())
                {
                    continue;
                }

                var sourceId = edge.SourceSymbolId;
                if (!seen.Add(sourceId))
                    continue;

                var item = context.BuildCapsuleItem(sourceId, edge.Kind, edge.Provenance,
                    "Framework route or handler entry point that reaches the anchor.");
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }

        return results;
    }
}
