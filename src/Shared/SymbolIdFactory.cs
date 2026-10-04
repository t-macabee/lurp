using Microsoft.CodeAnalysis;

namespace Lurp.Shared;

internal static class SymbolIdFactory
{
    // Always prefer the symbol's OWN assembly; fall back to the ambient
    // compilation identity only when ContainingAssembly is null (e.g. some
    // constructed/error symbols).
    internal static string? Make(ISymbol symbol, string ambientAssemblyIdentity)
    {
        // Normalize constructed symbols (closed generics like ReferenceCrudController<X,Y,Z>,
        // instantiated generic methods) to their original definition. Only definitions are
        // snapshot members, so an edge endpoint carrying an instantiated ID can never match
        // a declared symbol and is silently removed by DeleteOrphanEdges — the relationship
        // (e.g. Inherits to an internal generic base) was lost from the snapshot entirely.
        // Instantiation detail, where a consumer needs it, travels in TypeArgumentsJson.
        // Extension methods bind at call sites in reduced form (receiver parameter
        // removed), whose doc-comment ID omits the first parameter and so never matches
        // the declared method either — un-reduce before taking the original definition.
        if (symbol is IMethodSymbol { ReducedFrom: not null } reducedMethod)
            symbol = reducedMethod.ReducedFrom;
        symbol = symbol.OriginalDefinition;
        symbol = NormalizeExtensionBlockMember(symbol);
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        var identity = symbol.ContainingAssembly?.Identity.GetDisplayName() ?? ambientAssemblyIdentity;
        return $"{docCommentId}|{identity}";
    }

    /// <summary>
    ///     A C# 14 extension-block member binds, at the call site, to the
    ///     compiler-generated nested extension type, whose symbol is not the
    ///     declared member. Map it back to the declared extension member on the
    ///     outer static class so every edge endpoint names the stable symbol.
    /// </summary>
    private static ISymbol NormalizeExtensionBlockMember(ISymbol member)
    {
        if (member.ContainingType is not { IsExtension: true, ContainingType: { } outer })
            return member;

        switch (member)
        {
            case IMethodSymbol method:
                var extensionParameter = member.ContainingType.ExtensionParameter;
                foreach (var candidate in outer.GetMembers(method.Name).OfType<IMethodSymbol>())
                {
                    if (candidate.Parameters.Length != method.Parameters.Length + 1)
                        continue;
                    if (extensionParameter != null &&
                        !SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, extensionParameter.Type))
                        continue;
                    if (candidate.Parameters.Skip(1).Zip(method.Parameters)
                        .All(pair => SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type)))
                        return candidate.OriginalDefinition;
                }

                break;
            case IPropertySymbol property:
                foreach (var candidate in outer.GetMembers(property.Name).OfType<IPropertySymbol>())
                {
                    if (SymbolEqualityComparer.Default.Equals(candidate.Type, property.Type))
                        return candidate.OriginalDefinition;
                }

                break;
        }

        return member;
    }
}