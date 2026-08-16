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

                foreach (var param in method.Parameters)
                {
                    if (param.Type == null)
                        continue;

                    var paramTypeId = context.MakeSymbolId(param.Type);
                    if (paramTypeId == null)
                        continue;

                    var methodSyntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(method);
                    context.RecordFilteredExternal(param.Type, methodSyntax);

                    var key = (methodId, paramTypeId, nameof(EdgeKind.References));
                    if (!seen.Add(key))
                        continue;

                    var loc = context.GetMemberSourceLocation(method);
                    edges.Add(context.MakeEdge(methodId, paramTypeId, nameof(EdgeKind.References),
                        ExtractorConstants.ParameterDependenciesExtractor, loc));
                }
            }

        return edges;
    }
}