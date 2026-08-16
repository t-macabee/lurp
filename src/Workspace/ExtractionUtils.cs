using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

/// <summary>
///     Extraction helpers that carry no per-run state, factored out of the
///     extraction-context types so the contexts hold only what they own.
/// </summary>
internal static class ExtractionUtils
{
    /// <summary>
    ///     Every named type declared under <paramref name="ns" />, recursing into
    ///     child namespaces. Nested types are reached through their containing
    ///     type's own <c>GetTypeMembers()</c> by callers that need them.
    /// </summary>
    internal static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers()) yield return type;

        foreach (var childNs in ns.GetNamespaceMembers())
            foreach (var type in GetNamespaceTypeMembers(childNs))
                yield return type;
    }

    /// <summary>
    ///     True when <paramref name="syntaxTree" /> falls inside the extraction scope.
    ///     A null <paramref name="scopeDocuments" /> means "whole compilation"; a tree
    ///     with no file path has no document to scope by and stays in scope.
    /// </summary>
    /// <remarks>
    ///     Single definition of the scope predicate shared by
    ///     <see cref="SymbolExtractionContext" /> and
    ///     <c>Lurp.Adapters.AdapterExtractionContext</c>: absolute,
    ///     forward-slash-normalized path compare.
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

    /// <summary>
    ///     Every method-like declaration (methods, constructors, accessors) owned by
    ///     the given types, paired with its syntax node. When <paramref name="inScope" />
    ///     is provided, declarations whose declaring syntax tree is out of scope are
    ///     skipped; a null predicate leaves every declaration in.
    /// </summary>
    internal static IEnumerable<(IMethodSymbol method, CSharpSyntaxNode syntax)> EnumerateMethodDeclarations(
        IEnumerable<INamedTypeSymbol> types,
        Func<SyntaxTree, bool>? inScope = null)
    {
        foreach (var typeSymbol in types)
            foreach (var member in typeSymbol.GetMembers())
            {
                switch (member)
                {
                    case IMethodSymbol method:
                        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                        {
                            if (inScope != null && !inScope(syntaxRef.SyntaxTree))
                                continue;
                            var syntax = syntaxRef.GetSyntax();
                            switch (syntax)
                            {
                                case MethodDeclarationSyntax methodSyntax:
                                    yield return (method, methodSyntax);
                                    break;
                                case ConstructorDeclarationSyntax ctorSyntax:
                                    yield return (method, ctorSyntax);
                                    break;
                            }
                        }
                        break;
                    case IPropertySymbol property:
                        foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
                        {
                            if (accessor == null)
                                continue;

                            foreach (var syntaxRef in accessor.DeclaringSyntaxReferences)
                            {
                                if (inScope != null && !inScope(syntaxRef.SyntaxTree))
                                    continue;
                                if (syntaxRef.GetSyntax() is AccessorDeclarationSyntax accessorSyntax)
                                    yield return (accessor, accessorSyntax);
                            }
                        }
                        break;
                }
            }
    }

    internal static SyntaxNode? GetMethodBody(CSharpSyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => m.Body ?? (SyntaxNode?)m.ExpressionBody,
            ConstructorDeclarationSyntax c => c.Body ?? (SyntaxNode?)c.ExpressionBody,
            AccessorDeclarationSyntax a => a.Body ?? (SyntaxNode?)a.ExpressionBody,
            _ => null
        };
    }
}