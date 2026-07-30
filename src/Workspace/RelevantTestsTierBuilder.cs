using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class RelevantTestsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "relevantTests";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();

        AddTestsFor(context.SymbolId.Value);

        // TestAdapter records TestedBy as production -> test. A method anchor may
        // not be the exact production symbol recorded by the adapter, but its
        // upstream callers still identify the test method that exercises it.
        // Hop.SourceSymbolId is the upstream production caller; AddTestsFor queries
        // its outgoing TestedBy edges and collects target test IDs.
        var allowedKinds = new HashSet<string> { EdgeKind.Calls.ToString() };
        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);
        var paths = traverser.TraceImpact(
            context.SymbolId.Value,
            ImpactDirection.Upstream,
            allowedKinds,
            context.MaxHops);

        foreach (var path in paths)
        {
            foreach (var hop in path.Hops)
                AddTestsFor(hop.SourceSymbolId);
        }

        return results;

        void AddTestsFor(string productionOrTestSymbolId)
        {
            var outgoingEdges = context.EdgeStore.GetOutgoingEdges(context.SnapshotId, productionOrTestSymbolId);
            foreach (var edge in outgoingEdges)
            {
                if (edge.Kind != EdgeKind.TestedBy.ToString())
                    continue;

                var testSymbolId = edge.TargetSymbolId;
                if (!seen.Add(testSymbolId))
                    continue;

                var item = context.BuildCapsuleItem(testSymbolId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }
    }
}
