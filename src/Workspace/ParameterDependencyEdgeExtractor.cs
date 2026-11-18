using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class ParameterDependencyEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is not IMethodSymbol method || !ExtractionUtils.HasStructuralFacts(method))
                    continue;

                // D8, D23: the delegate declaration names the parameter types of its Invoke,
                // so the delegate type is the source.
                if (ExtractionUtils.SignatureOwner(method) is not { } sourceSymbol || !context.IsMemberInScope(sourceSymbol))
                    continue;

                var sourceId = context.MakeSymbolId(sourceSymbol);
                if (sourceId == null)
                    continue;

                var sourceSyntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(sourceSymbol);
                var loc = context.GetMemberSourceLocation(sourceSymbol);
                var signatureTypes = method.Parameters
                    .Select(param => (Type: param.Type, Location: ParameterTypeLocation(param)))
                    .ToList();
                // B58: a block member's receiver is declared on the block, not in the member's parameter list.
                if (method.ContainingType is { IsExtension: true, ExtensionParameter: { } receiver })
                    signatureTypes.Add((receiver.Type, null));

                foreach (var (signatureType, parameterLocation) in signatureTypes)
                {
                    foreach (var usedType in ExtractionUtils.TypeUses(signatureType))
                    {
                        var usedTypeId = context.MakeSymbolId(usedType);
                        if (usedTypeId == null)
                            continue;

                        context.RecordFilteredExternal(usedType, sourceSyntax);

                        // The edge names the parameter's type, so it is located at that
                        // type's syntax. For a primary constructor Roslyn reports the
                        // type declaration as the parameter's declaring syntax, so the
                        // member span would be the whole type and any member edit would
                        // move every parameter edge.
                        edges.Add(context.MakeEdge(sourceId, usedTypeId, nameof(EdgeKind.References),
                            ExtractorConstants.ParameterDependenciesExtractor, parameterLocation ?? loc));
                    }
                }
            }

        return edges;
    }

    /// <summary>
    ///     Source location of the parameter's type syntax, or null when the parameter
    ///     has none (an extension block's receiver).
    /// </summary>
    private (string? path, int? sl, int? sc, int? el, int? ec)? ParameterTypeLocation(IParameterSymbol parameter)
    {
        foreach (var syntaxRef in parameter.DeclaringSyntaxReferences)
            if (syntaxRef.GetSyntax() is ParameterSyntax { Type: { } typeSyntax } && !typeSyntax.IsMissing)
                return context.GetLocationInfo(typeSyntax.GetLocation());

        return null;
    }
}