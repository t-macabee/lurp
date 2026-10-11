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

                var isDelegateInvoke = method.MethodKind == MethodKind.DelegateInvoke;
                if (member.IsImplicitlyDeclared && !isDelegateInvoke)
                    continue;

                // A delegate's implicit Invoke names its parameter types in the delegate
                // declaration, so the delegate type is the source.
                var sourceSymbol = isDelegateInvoke ? (ISymbol)method.ContainingType! : method;

                if (!context.IsMemberInScope(sourceSymbol))
                    continue;

                var sourceId = context.MakeSymbolId(sourceSymbol);
                if (sourceId == null)
                    continue;

                var sourceSyntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(sourceSymbol);
                var loc = context.GetMemberSourceLocation(sourceSymbol);
                var signatureTypes = method.Parameters.Select(param => param.Type);
                // B58: a block member's receiver is declared on the block, not in the member's parameter list.
                if (method.ContainingType is { IsExtension: true, ExtensionParameter: { } receiver })
                    signatureTypes = signatureTypes.Append(receiver.Type);

                foreach (var signatureType in signatureTypes)
                {
                    foreach (var usedType in ExtractionUtils.TypeUses(signatureType))
                    {
                        var usedTypeId = context.MakeSymbolId(usedType);
                        if (usedTypeId == null)
                            continue;

                        context.RecordFilteredExternal(usedType, sourceSyntax);

                        var key = (sourceId, usedTypeId, nameof(EdgeKind.References));
                        if (!seen.Add(key))
                            continue;

                        edges.Add(context.MakeEdge(sourceId, usedTypeId, nameof(EdgeKind.References),
                            ExtractorConstants.ParameterDependenciesExtractor, loc));
                    }
                }
            }

        return edges;
    }
}