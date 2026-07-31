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
            QueryTestedBy(productionOrTestSymbolId);

            // TestAdapter emits TestedBy on the containing type, not on member
            // symbols. Derive the containing-type ID and query its edges too.
            var typeId = DeriveContainingTypeId(productionOrTestSymbolId);
            if (typeId != null && typeId != productionOrTestSymbolId)
                QueryTestedBy(typeId);
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

                var item = context.BuildCapsuleItem(testSymbolId, edge.Kind, edge.Provenance);
                if (item != null)
                {
                    results.Add(item);
                }
            }
        }

        static string? DeriveContainingTypeId(string symbolId)
        {
            // symbolId format: docCommentId|assemblyIdentity.
            // Strip the member segment from the doc-comment ID (e.g.,
            // M:A.B.C.Method → T:A.B.C) and rebuild with the same assembly.
            var pipeIndex = symbolId.IndexOf('|');
            if (pipeIndex < 0)
                return null;

            var docCommentId = symbolId.AsSpan(0, pipeIndex);
            if (docCommentId.Length < 3 || docCommentId[1] != ':')
                return null;

            var kind = docCommentId[0];
            if (kind == 'T' || kind == 'N')
                return null; // Already a type or namespace — no parent type

            var afterPrefix = docCommentId[2..];
            var lastDot = afterPrefix.LastIndexOf('.');
            if (lastDot < 0)
                return null;

            var parentTypeName = afterPrefix[..lastDot];
            var assemblyIdentity = symbolId.AsSpan(pipeIndex + 1);
            return string.Concat("T:".AsSpan(), parentTypeName, "|".AsSpan(), assemblyIdentity);
        }
    }
}
