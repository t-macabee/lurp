using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
///     <c>Overrides</c>: a method, property or event that overrides a member points to the member it
///     overrides. <c>Hides</c>: a member that is not an override points to each member that it hides
///     by the C# rules of hiding through inheritance (S01-F17).
/// </summary>
internal sealed class OverridesEdgeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();

        foreach (var typeSymbol in context.GetAllNamedTypes())
            foreach (var member in typeSymbol.GetMembers())
            {
                if (!ExtractionUtils.HasStructuralFacts(member) || !context.IsMemberInScope(member))
                    continue;

                if (ExtractionUtils.OverriddenMember(member) is { } overridden)
                    EmitEdge(member, overridden, nameof(EdgeKind.Overrides), ExtractorConstants.OverridesExtractor, edges);
                else
                    EmitHidesEdges(member, edges);
            }

        return edges;
    }

    /// <summary>
    ///     C# hiding through inheritance: a member hides the members with its name that its type
    ///     inherits. The walk goes up the base types to the first one with a hidden member: a member
    ///     above it is hidden there already, or that base type hides it from the derived type. Only a
    ///     member that the derived type can access is inherited.
    /// </summary>
    private void EmitHidesEdges(ISymbol member, List<EdgeRecord> edges)
    {
        if (member.ContainingType is not { } containingType)
            return;

        for (var baseType = containingType.BaseType; baseType != null; baseType = baseType.BaseType)
        {
            var hidden = baseType.GetMembers(member.Name)
                .Where(baseMember => Hides(member, baseMember) &&
                                     context.Compilation.IsSymbolAccessibleWithin(baseMember, containingType))
                .ToList();
            foreach (var baseMember in hidden)
                EmitEdge(member, baseMember, nameof(EdgeKind.Hides), ExtractorConstants.HidesExtractor, edges);
            if (hidden.Count > 0)
                return;
        }
    }

    /// <summary>
    ///     C#: a method hides a base method with the same signature, and an ordinary method also hides
    ///     every base member that is not a method. An indexer hides a base indexer with the same
    ///     parameters. A field, property, event or nested type hides every base member with its name
    ///     (a nested type: a base type with the same arity). A constructor, a static constructor and a
    ///     destructor hide nothing.
    /// </summary>
    private static bool Hides(ISymbol member, ISymbol baseMember)
    {
        return (member, baseMember) switch
        {
            (IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor }, _) => false,
            (IMethodSymbol method, IMethodSymbol baseMethod) => SameSignature(method, baseMethod),
            (IMethodSymbol method, _) => method.MethodKind == MethodKind.Ordinary,
            (IPropertySymbol { IsIndexer: true } indexer, IPropertySymbol { IsIndexer: true } baseIndexer) =>
                SameParameters(indexer.Parameters, baseIndexer.Parameters),
            (IPropertySymbol { IsIndexer: true }, _) => false,
            (INamedTypeSymbol type, INamedTypeSymbol baseType) => type.Arity == baseType.Arity,
            _ => true
        };
    }

    /// <summary>
    ///     C# signature of a method: the number of type parameters and, for each parameter in order,
    ///     its type and its passing mode (by value, or by reference: ref, out and in). The return type
    ///     is not part of it. Method type parameters match by position.
    /// </summary>
    private static bool SameSignature(IMethodSymbol method, IMethodSymbol baseMethod)
    {
        if (method.Arity != baseMethod.Arity)
            return false;

        var comparable = baseMethod.Arity == 0 ? baseMethod : baseMethod.Construct([.. method.TypeParameters]);
        return SameParameters(method.Parameters, comparable.Parameters);
    }

    private static bool SameParameters(IReadOnlyList<IParameterSymbol> parameters, IReadOnlyList<IParameterSymbol> baseParameters)
    {
        return parameters.Count == baseParameters.Count &&
               parameters.Zip(baseParameters).All(static pair =>
                   (pair.First.RefKind == RefKind.None) == (pair.Second.RefKind == RefKind.None) &&
                   SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type));
    }

    private void EmitEdge(ISymbol member, ISymbol target, string kind, string extractorVersion, List<EdgeRecord> edges)
    {
        context.RecordFilteredExternal(target, BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(member));
        var sourceId = context.MakeSymbolId(member);
        var targetId = context.MakeSymbolId(target);
        if (sourceId == null || targetId == null)
            return;

        edges.Add(context.MakeEdge(sourceId, targetId, kind, extractorVersion, context.GetMemberSourceLocation(member)));
    }
}
