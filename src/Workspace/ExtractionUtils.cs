using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

/// <summary>
/// Extraction helpers that carry no per-run state, factored out of the
/// extraction-context types so the contexts hold only what they own.
/// </summary>
internal static class ExtractionUtils
{
    /// <summary>
    /// Every named type declared under <paramref name="ns"/>, recursing into
    /// child namespaces. Nested types are reached through their containing
    /// type's own <c>GetTypeMembers()</c> by callers that need them.
    /// </summary>
    internal static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetNamespaceTypeMembers(childNs))
            {
                yield return type;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="syntaxTree"/> falls inside the extraction scope.
    /// A null <paramref name="scopeDocuments"/> means "whole compilation"; a tree
    /// with no file path has no document to scope by and stays in scope.
    /// </summary>
    /// <remarks>
    /// Single definition of the scope predicate shared by
    /// <see cref="SymbolExtractionContext"/> and
    /// <c>Lurp.Adapters.AdapterExtractionContext</c>: absolute,
    /// forward-slash-normalized path compare.
    /// </remarks>
    internal static bool IsInScope(IReadOnlySet<string>? scopeDocuments, SyntaxTree? syntaxTree)
    {
        if (scopeDocuments == null || syntaxTree == null)
            return true;
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return true;
        return scopeDocuments.Contains(PathNormalizer.ToForwardSlash(filePath));
    }
}
