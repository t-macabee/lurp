using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ReturnsEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method || !ExtractionUtils.HasStructuralFacts(method))
                    continue;

                // D23: a delegate type returns the return type of its Invoke.
                if (ExtractionUtils.SignatureOwner(method) is not { } owner || !context.IsMemberInScope(owner))
                    continue;

                if (method.ReturnsVoid || method.ReturnType == null)
                    continue;

                var ownerId = context.MakeSymbolId(owner);
                var returnTypeId = context.MakeSymbolId(method.ReturnType);
                if (ownerId == null || returnTypeId == null)
                    continue;

                context.RecordFilteredExternal(method.ReturnType, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(owner));

                var key = (ownerId, returnTypeId, nameof(EdgeKind.Returns));
                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(owner);
                edges.Add(context.MakeEdge(ownerId, returnTypeId, nameof(EdgeKind.Returns),
                    ExtractorConstants.ReturnsExtractor, loc));
            }

        return edges;
    }
}