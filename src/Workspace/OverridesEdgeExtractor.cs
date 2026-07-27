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
        if (member is IMethodSymbol method)
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

                var sourceId = context.MakeSymbolId(method);
                var targetId = context.MakeSymbolId(baseMethod);
                if (sourceId == null || targetId == null)
                    continue;

                var key = (sourceId, targetId, EdgeKind.Hides.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(method);
                edges.Add(context.MakeEdge(sourceId, targetId, EdgeKind.Hides.ToString(),
                    ExtractorConstants.HidesExtractor, loc));
            }
        }
        else if (member is IPropertySymbol prop)
        {
            if (prop.IsOverride)
                return;

            foreach (var baseMember in baseType.GetMembers(prop.Name))
            {
                if (baseMember is not IPropertySymbol baseProp)
                    continue;
                if (!IsAccessible(baseProp, containingType))
                    continue;

                var sourceId = context.MakeSymbolId(prop);
                var targetId = context.MakeSymbolId(baseProp);
                if (sourceId == null || targetId == null)
                    continue;

                var key = (sourceId, targetId, EdgeKind.Hides.ToString());
                if (!seen.Add(key))
                    continue;

                var loc = context.GetMemberSourceLocation(prop);
                edges.Add(context.MakeEdge(sourceId, targetId, EdgeKind.Hides.ToString(),
                    ExtractorConstants.HidesExtractor, loc));
            }
        }
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
