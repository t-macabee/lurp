using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class RegisteredImplementationsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "registeredImplementations";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();

        var incomingKinds = new HashSet<string>
        {
            EdgeKind.MayDispatchTo.ToString(),
            EdgeKind.Registers.ToString(),
        };
        var outgoingKinds = new HashSet<string>
        {
            EdgeKind.MayDispatchTo.ToString(),
            EdgeKind.Handles.ToString(),
            EdgeKind.Registers.ToString(),
        };

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            var incomingEdges = context.EdgeStore.GetIncomingEdges(context.SnapshotId, symbolId);
            foreach (var edge in incomingEdges)
            {
                if (!incomingKinds.Contains(edge.Kind))
                    continue;

                var sourceId = edge.SourceSymbolId;
                if (!seen.Add(sourceId))
                    continue;

                var item = context.BuildCapsuleItem(sourceId, edge.Kind, edge.Provenance,
                    "Persisted runtime dispatch or registration source for the anchor.");
                if (item != null)
                {
                    results.Add(item);
                }
            }

            var outgoingEdges = context.EdgeStore.GetOutgoingEdges(context.SnapshotId, symbolId);
            foreach (var edge in outgoingEdges)
            {
                if (!outgoingKinds.Contains(edge.Kind))
                    continue;

                var targetId = edge.TargetSymbolId;
                if (!seen.Add(targetId))
                    continue;

                var item = context.BuildCapsuleItem(targetId, edge.Kind, edge.Provenance,
                    "Persisted runtime dispatch, handler, or registration target of the anchor.");
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }

        return results;
    }
}
