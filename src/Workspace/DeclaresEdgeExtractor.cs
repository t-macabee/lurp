using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DeclaresEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            var typeId = context.MakeSymbolId(typeSymbol);

            if (typeId == null)
                continue;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol)
                    continue;

                if (member.IsImplicitlyDeclared)
                    continue;

                if (!context.IsMemberInScope(member))
                    continue;

                var memberId = context.MakeSymbolId(member);

                if (memberId == null)
                    continue;

                var key = (typeId, memberId, nameof(EdgeKind.Declares));

                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(member);

                // An edge without a resolvable document path (e.g. a
                // compiler-synthesized member whose syntax reference has no
                // path) is useless for incremental scoping and forces the
                // CrossDocumentEdgeRefresher's expensive fallback.
                if (loc == null)
                    continue;

                edges.Add(context.MakeEdge(typeId, memberId, nameof(EdgeKind.Declares), ExtractorConstants.DeclaresExtractor, loc));
            }
        }

        return edges;
    }
}