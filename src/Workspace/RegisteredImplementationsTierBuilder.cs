using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class RegisteredImplementationsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    string IContextTierBuilder.Name => "registered_implementations";

    string IContextTierBuilder.InclusionReason => "Persisted dispatch, registration, or handler targets relevant at runtime.";

    string? IContextTierBuilder.EmptyReason =>
        context.HasUnmodeledRegistrations() ? "unmodeled_construct" : null;

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();
        var seen = new HashSet<string>();

        var incomingKinds = new HashSet<string>
        {
            nameof(EdgeKind.MayDispatchTo),
            nameof(EdgeKind.Registers)
        };
        var outgoingKinds = new HashSet<string>
        {
            nameof(EdgeKind.MayDispatchTo),
            nameof(EdgeKind.Handles),
            nameof(EdgeKind.Registers)
        };

        // DI registration is a type-level fact: `AddHostedService<TPublisher>()`
        // registers the type, never the member the capsule is anchored on. A
        // member anchor must therefore also consult its declaring type, or the
        // registration that put the anchor on the runtime path is invisible in
        // the tier named after it (it would surface only as an uncertainty).
        // The type-anchor direction is already covered: EffectiveSymbolIds
        // expands a type anchor to its declared members.
        foreach (var symbolId in RegistrationScopeIds())
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
                if (item != null) results.Add(item);
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
                if (item != null) results.Add(item);
            }
        }

        return results;
    }

    private List<string> RegistrationScopeIds()
    {
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var symbolId in context.EffectiveSymbolIds)
        {
            if (seen.Add(symbolId))
                ids.Add(symbolId);

            foreach (var typeId in context.GetDeclaringTypeIds(symbolId))
                if (seen.Add(typeId))
                    ids.Add(typeId);
        }

        return ids;
    }
}