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
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return null;
        var identity = symbol.ContainingAssembly?.Identity.GetDisplayName() ?? ambientAssemblyIdentity;
        return $"{docCommentId}|{identity}";
    }
}
