using Lurp.Storage;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class OverridesEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (!context.IsMemberInScope(member))
                    continue;

                // -- Override edges --
                ISymbol? overridden = member switch
                {
                    IMethodSymbol method when method.IsOverride && method.OverriddenMethod != null
                        => method.OverriddenMethod,
                    IPropertySymbol prop when prop.IsOverride && prop.OverriddenProperty != null
                        => prop.OverriddenProperty,
                    _ => null
                };
                if (overridden != null)
                    context.RecordFilteredExternal(overridden, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(member));

                (string? sourceId, string? targetId) = member switch
                {
                    IMethodSymbol method when method.IsOverride && method.OverriddenMethod != null
                        => (context.MakeSymbolId(method), context.MakeSymbolId(method.OverriddenMethod)),
                    IPropertySymbol prop when prop.IsOverride && prop.OverriddenProperty != null
                        => (context.MakeSymbolId(prop), context.MakeSymbolId(prop.OverriddenProperty)),
                    _ => ((string?)null, (string?)null)
                };

                if (sourceId != null && targetId != null)
                {
                    var key = (sourceId, targetId, EdgeKind.Overrides.ToString());
                    if (seen.Add(key))
                    {
                        var loc = context.GetMemberSourceLocation(member);
                        edges.Add(context.MakeEdge(sourceId, targetId, EdgeKind.Overrides.ToString(),
                            ExtractorConstants.OverridesExtractor, loc));
                    }
                }

                // -- Hides edges (new member-hiding) --
                EmitHidesEdges(member, edges, seen);
            }
        }

        return edges;
    }

    private void EmitHidesEdges(ISymbol member, List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        var containingType = member.ContainingType;
        if (containingType == null)
            return;

        var baseType = containingType.BaseType;
        if (baseType == null)
            return;

        // Only consider members that are NOT overrides (overrides already handled above)
        switch (member)
        {
            case IMethodSymbol method:
                EmitHidesMethodEdges(method, containingType, baseType, edges, seen);
                break;
            case IPropertySymbol prop:
                EmitHidesPropertyEdges(prop, containingType, baseType, edges, seen);
                break;
        }
    }

    private void EmitHidesMethodEdges(IMethodSymbol method, INamedTypeSymbol containingType,
        INamedTypeSymbol baseType, List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        if (method.IsOverride || method.MethodKind == MethodKind.Constructor ||
            method.MethodKind == MethodKind.StaticConstructor ||
            method.MethodKind == MethodKind.Destructor)
            return;

        foreach (var baseMember in baseType.GetMembers(method.Name))
        {
            if (baseMember is not IMethodSymbol baseMethod)
                continue;
            if (!IsAccessible(baseMethod, containingType))
                continue;
            if (!ParametersMatch(method, baseMethod))
                continue;

            TryEmitHidesEdge(method, baseMethod, edges, seen);
        }
    }

    private void EmitHidesPropertyEdges(IPropertySymbol prop, INamedTypeSymbol containingType,
        INamedTypeSymbol baseType, List<EdgeRecord> edges,
        HashSet<(string source, string target, string kind)> seen)
    {
        if (prop.IsOverride)
            return;

        foreach (var baseMember in baseType.GetMembers(prop.Name))
        {
            if (baseMember is not IPropertySymbol baseProp)
                continue;
            if (!IsAccessible(baseProp, containingType))
                continue;

            TryEmitHidesEdge(prop, baseProp, edges, seen);
        }
    }

    private void TryEmitHidesEdge(ISymbol sourceSymbol, ISymbol targetSymbol,
        List<EdgeRecord> edges, HashSet<(string source, string target, string kind)> seen)
    {
        var sourceId = context.MakeSymbolId(sourceSymbol);
        var targetId = context.MakeSymbolId(targetSymbol);
        if (sourceId == null || targetId == null)
            return;

        context.RecordFilteredExternal(targetSymbol, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(sourceSymbol));

        var key = (sourceId, targetId, EdgeKind.Hides.ToString());
        if (!seen.Add(key))
            return;

        var loc = context.GetMemberSourceLocation(sourceSymbol);
        edges.Add(context.MakeEdge(sourceId, targetId, EdgeKind.Hides.ToString(),
            ExtractorConstants.HidesExtractor, loc));
    }

    private static bool IsAccessible(ISymbol baseMember, INamedTypeSymbol derivedType)
    {
        return baseMember.DeclaredAccessibility switch
        {
            Accessibility.Public => true,
            Accessibility.Protected => true,
            Accessibility.ProtectedOrInternal => true,
            Accessibility.ProtectedAndInternal => SymbolEqualityComparer.Default.Equals(
                derivedType.ContainingAssembly, baseMember.ContainingAssembly),
            Accessibility.Internal => SymbolEqualityComparer.Default.Equals(
                derivedType.ContainingAssembly, baseMember.ContainingAssembly),
            _ => false
        };
    }

    private static bool ParametersMatch(IMethodSymbol method, IMethodSymbol baseMethod)
    {
        if (method.Parameters.Length != baseMethod.Parameters.Length)
            return false;

        for (int i = 0; i < method.Parameters.Length; i++)
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    method.Parameters[i].Type, baseMethod.Parameters[i].Type))
                return false;
        }

        return true;
    }
}
