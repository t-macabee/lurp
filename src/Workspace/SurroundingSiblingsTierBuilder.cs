using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class SurroundingSiblingsTierBuilder(ContextTierContext context) : IContextTierBuilder
{
    private const string SiblingInclusionReason =
        "Sibling declaration sharing the anchor's containing declaration.";

    string IContextTierBuilder.Name => "surroundingSource";

    List<CapsuleItem> IContextTierBuilder.Build()
    {
        var results = new List<CapsuleItem>();

        if (IsTypeAnchor(context.SymbolId))
        {
            // A type anchor's surrounding/local source is its own declared
            // members (and the nested types it contains). Namespaces are not
            // graph nodes, so no namespace sibling relationship is invented.
            AddSiblings(results, parentId: context.SymbolId.Value, includeNestedTypes: true);

            // Nested types retain distinct Contains semantics: a nested type's
            // siblings are the other nested types of its containing type.
            var containingParent = FindContainingParentId();
            if (containingParent != null)
                AddSiblings(results, containingParent, includeNestedTypes: false);

            return results;
        }

        // A member anchor resolves its containing type through the incoming
        // Declares edge (type -> member); Contains is accepted where it carries
        // distinct nested-type containment semantics.
        var parentId = FindContainingParentId();
        if (parentId == null)
            return results;

        AddSiblings(results, parentId, includeNestedTypes: true);
        return results;
    }

    private static bool IsTypeAnchor(SymbolId symbolId)
        => symbolId.DocCommentId.Length >= 2
           && symbolId.DocCommentId[0] == 'T'
           && symbolId.DocCommentId[1] == ':';

    private string? FindContainingParentId()
    {
        foreach (var edge in context.EdgeStore.GetIncomingEdges(context.SnapshotId, context.SymbolId.Value))
        {
            if (edge.Kind == EdgeKind.Declares.ToString() || edge.Kind == EdgeKind.Contains.ToString())
                return edge.SourceSymbolId;
        }
        return null;
    }

    private void AddSiblings(List<CapsuleItem> results, string parentId, bool includeNestedTypes)
    {
        foreach (var edge in context.EdgeStore.GetOutgoingEdges(context.SnapshotId, parentId))
        {
            if (edge.Kind == EdgeKind.Declares.ToString()
                || (includeNestedTypes && edge.Kind == EdgeKind.Contains.ToString()))
            {
                if (edge.TargetSymbolId == context.SymbolId.Value)
                    continue;

                var item = context.BuildCapsuleItem(edge.TargetSymbolId, edge.Kind, edge.Provenance,
                    SiblingInclusionReason);
                if (item != null)
                    results.Add(item);
            }
        }
    }
}
