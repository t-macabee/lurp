using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ParameterDependencyEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method)
                    continue;

                if (member.IsImplicitlyDeclared)
                    continue;

                if (!context.IsMemberInScope(method))
                    continue;

                var methodId = context.MakeSymbolId(method);
                if (methodId == null)
                    continue;

                var methodSyntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(method);
                var loc = context.GetMemberSourceLocation(method);
                foreach (var param in method.Parameters)
                {
                    foreach (var usedType in ExtractionUtils.TypeUses(param.Type))
                    {
                        var usedTypeId = context.MakeSymbolId(usedType);
                        if (usedTypeId == null)
                            continue;

                        context.RecordFilteredExternal(usedType, methodSyntax);

                        var key = (methodId, usedTypeId, nameof(EdgeKind.References));
                        if (!seen.Add(key))
                            continue;

                        edges.Add(context.MakeEdge(methodId, usedTypeId, nameof(EdgeKind.References),
                            ExtractorConstants.ParameterDependenciesExtractor, loc));
                    }
                }
            }

        return edges;
    }
}