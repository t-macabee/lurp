using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class RelevantTestsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "relevant_tests";

    string IContextTierBuilder.InclusionReason => "Persisted TestedBy evidence connected to the anchor or its upstream callers.";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();

        foreach (var symbolId in context.EffectiveSymbolIds)
            AddTestsFor(symbolId);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            foreach (var dispatchEdge in context.GetDispatchSourceEdges(symbolId))
                AddTestsFor(dispatchEdge.SourceSymbolId);
        }

        var allowedKinds = new HashSet<string> { EdgeKind.Calls.ToString() };
        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);

        foreach (var symbolId in context.EffectiveSymbolIds)
            AddTestsForUpstreamCallers(symbolId);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            foreach (var dispatchEdge in context.GetDispatchSourceEdges(symbolId))
                AddTestsForUpstreamCallers(dispatchEdge.SourceSymbolId);
        }

        return results;

        void AddTestsFor(string productionOrTestSymbolId)
        {
            foreach (var candidateId in TestSymbolDiscovery.ExpandProductionSymbolIds(productionOrTestSymbolId))
                QueryTestedBy(candidateId);
        }

        void AddTestsForUpstreamCallers(string symbolId)
        {
            var paths = traverser.TraceImpact(
                symbolId,
                ImpactDirection.Upstream,
                allowedKinds,
                context.MaxHops);

            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                    AddTestsFor(hop.SourceSymbolId);
            }
        }

        void QueryTestedBy(string symbolId)
        {
            var outgoingEdges = context.EdgeStore.GetOutgoingEdges(context.SnapshotId, symbolId);
            foreach (var edge in outgoingEdges)
            {
                if (edge.Kind != EdgeKind.TestedBy.ToString())
                    continue;

                var testSymbolId = edge.TargetSymbolId;
                if (!seen.Add(testSymbolId))
                    continue;

                var item = context.BuildCapsuleItem(testSymbolId, edge.Kind, edge.Provenance,
                    "Persisted TestedBy evidence connects this test to the change scope.");
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }

    }
}
