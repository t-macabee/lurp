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
    ///     Every method-like declaration (methods, constructors, accessors, operators,
    ///     conversion operators, destructors) owned by the given types, paired with its
    ///     syntax node. When <paramref name="inScope" /> is provided, declarations whose
    ///     declaring syntax tree is out of scope are skipped; a null predicate leaves
    ///     every declaration in. A partial method is yielded for both its definition and
    ///     its implementation part.
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
                        foreach (var part in MethodParts(method))
                        foreach (var syntaxRef in part.DeclaringSyntaxReferences)
                        {
                            if (inScope != null && !inScope(syntaxRef.SyntaxTree))
                                continue;
                            var syntax = syntaxRef.GetSyntax();
                            switch (syntax)
                            {
                                case MethodDeclarationSyntax methodSyntax:
                                    yield return (part, methodSyntax);
                                    break;
                                case ConstructorDeclarationSyntax ctorSyntax:
                                    yield return (part, ctorSyntax);
                                    break;
                                case OperatorDeclarationSyntax operatorSyntax:
                                    yield return (part, operatorSyntax);
                                    break;
                                case ConversionOperatorDeclarationSyntax conversionSyntax:
                                    yield return (part, conversionSyntax);
                                    break;
                                case DestructorDeclarationSyntax destructorSyntax:
                                    yield return (part, destructorSyntax);
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
                                var accessorSyntax = syntaxRef.GetSyntax();
                                switch (accessorSyntax)
                                {
                                    case AccessorDeclarationSyntax accessorDeclaration:
                                        yield return (accessor, accessorDeclaration);
                                        break;
                                    // An expression-bodied property/indexer has no accessor
                                    // declaration: the getter's declaring syntax is the property
                                    // itself, and its body is the arrow expression.
                                    case PropertyDeclarationSyntax propertyDeclaration
                                        when propertyDeclaration.ExpressionBody != null:
                                        yield return (accessor, propertyDeclaration);
                                        break;
                                    case IndexerDeclarationSyntax indexerDeclaration
                                        when indexerDeclaration.ExpressionBody != null:
                                        yield return (accessor, indexerDeclaration);
                                        break;
                                }
                            }
                        }

                        // An expression-bodied property's getter is compiler-synthesized
                        // and has no declaring syntax of its own; pair it with the
                        // property/indexer declaration from the property symbol.
                        if (property.GetMethod is { } getter)
                        {
                            foreach (var syntaxRef in property.DeclaringSyntaxReferences)
                            {
                                if (inScope != null && !inScope(syntaxRef.SyntaxTree))
                                    continue;
                                switch (syntaxRef.GetSyntax())
                                {
                                    case PropertyDeclarationSyntax { ExpressionBody: not null } propertyDeclaration:
                                        yield return (getter, propertyDeclaration);
                                        break;
                                    case IndexerDeclarationSyntax { ExpressionBody: not null } indexerDeclaration:
                                        yield return (getter, indexerDeclaration);
                                        break;
                                }
                            }
                        }

                        break;
                }
            }
    }

    private static IEnumerable<IMethodSymbol> MethodParts(IMethodSymbol method)
    {
        yield return method;
        if (method.PartialImplementationPart is { } implementation &&
            !SymbolEqualityComparer.Default.Equals(implementation, method))
            yield return implementation;
    }

    internal static SyntaxNode? GetMethodBody(CSharpSyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => m.Body ?? (SyntaxNode?)m.ExpressionBody,
            ConstructorDeclarationSyntax c => c.Body ?? (SyntaxNode?)c.ExpressionBody,
            AccessorDeclarationSyntax a => a.Body ?? (SyntaxNode?)a.ExpressionBody,
            OperatorDeclarationSyntax o => o.Body ?? (SyntaxNode?)o.ExpressionBody,
            ConversionOperatorDeclarationSyntax co => co.Body ?? (SyntaxNode?)co.ExpressionBody,
            DestructorDeclarationSyntax d => d.Body ?? (SyntaxNode?)d.ExpressionBody,
            PropertyDeclarationSyntax p => p.ExpressionBody,
            IndexerDeclarationSyntax i => i.ExpressionBody,
            _ => null
        };
    }
}