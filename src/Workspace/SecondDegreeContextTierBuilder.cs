using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SecondDegreeContextTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "secondDegreeContext";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var allowedKinds = new HashSet<string>
        {
            EdgeKind.Calls.ToString()
        };

        if (context.MaxHops <= 1)
            return results;

        var effectiveSymbolIds = context.EffectiveSymbolIds;
        var traverser = new ImpactTraverser(context.EdgeStore, context.SnapshotId);

        var seen = new HashSet<string>();

        const string directReason =
            "Bounded upstream dependency within the requested hop limit.";
        const string dispatchReason =
            "Upstream dependency reached through interface dispatch. Weaker evidence — "
          + "the runtime dispatch target may differ from the anchor.";

        foreach (var symbolId in effectiveSymbolIds)
            AddUpstreamNeighbors(symbolId, directReason);

        foreach (var symbolId in effectiveSymbolIds)
        {
            foreach (var dispatchEdge in context.GetDispatchSourceEdges(symbolId))
                AddUpstreamNeighbors(dispatchEdge.SourceSymbolId, dispatchReason);
        }

        return results;

        void AddUpstreamNeighbors(string symbolId, string inclusionReason)
        {
            var paths = traverser.TraceImpact(symbolId, ImpactDirection.Upstream, allowedKinds, maxDepth: context.MaxHops);
            foreach (var path in paths)
            {
                foreach (var hop in path.Hops)
                {
                    var neighborId = hop.SourceSymbolId;
                    if (!seen.Add(neighborId))
                        continue;

                    if (effectiveSymbolIds.Contains(neighborId))
                        continue;

                    var item = context.BuildCapsuleItem(neighborId, hop.EdgeKind, hop.Provenance,
                        inclusionReason);
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }
        }
    }
}
