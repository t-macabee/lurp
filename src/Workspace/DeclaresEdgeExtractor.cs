using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class DeclaresEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            var typeId = context.MakeSymbolId(typeSymbol);

            if (typeId == null)
                continue;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol)
                    continue;

                if (!ExtractionUtils.HasStructuralFacts(member))
                    continue;

                var memberId = context.MakeSymbolId(member);
                if (memberId == null)
                    continue;

                EmitMemberPart(typeId, member, memberId, edges);

                ISymbol? implPart = member switch
                {
                    IMethodSymbol { PartialImplementationPart: { } implMethod } => implMethod,
                    IPropertySymbol { PartialImplementationPart: { } implProperty } => implProperty,
                    _ => null
                };
                if (implPart != null && !SymbolEqualityComparer.Default.Equals(implPart, member))
                    EmitMemberPart(typeId, implPart, memberId, edges);
            }
        }

        return edges;
    }

    private void EmitMemberPart(string typeId, ISymbol memberPart, string memberId, List<EdgeRecord> edges)
    {
        if (!context.IsMemberInScope(memberPart))
            return;

        var loc = context.GetMemberSourceLocation(memberPart);

        // An edge without a resolvable document path (a declaration in a
        // tree with no path) is useless for incremental scoping and forces
        // ReverseEdgeClosure's expensive fallback.
        if (loc == null)
            return;

        edges.Add(context.MakeEdge(typeId, memberId, nameof(EdgeKind.Declares), ExtractorConstants.DeclaresExtractor, loc));
    }
}