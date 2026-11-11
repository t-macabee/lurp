using Microsoft.CodeAnalysis;

namespace Lurp.Tests;

/// <summary>
///     The documentation-comment id that the extractor persists for a Roslyn symbol, derived with
///     Roslyn APIs only (this file does not reference Lurp.Workspace): un-reduce a classic extension
///     call, take the original definition, then map a C# 14 extension-block method to its
///     implementation on the outer static class (AssociatedExtensionImplementation). A block property
///     keeps its own id. Oracle B uses it to compare Roslyn's callers with persisted edge endpoints.
/// </summary>
internal static class SymbolDocIds
{
    public static string? Normalized(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: not null } reduced)
            symbol = reduced.ReducedFrom;
        symbol = symbol.OriginalDefinition;
        if (symbol is IMethodSymbol { ContainingType.IsExtension: true } blockMethod)
            symbol = blockMethod.AssociatedExtensionImplementation?.OriginalDefinition ?? blockMethod;
        return symbol.GetDocumentationCommentId();
    }
}
