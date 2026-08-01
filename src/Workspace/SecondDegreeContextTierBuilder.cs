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
        foreach (var symbolId in effectiveSymbolIds)
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
                        "Bounded upstream dependency within the requested hop limit.");
                    if (item != null)
                    {
                        results.Add(item);
                    }
                }
            }
        }

        return results;
    }
}
