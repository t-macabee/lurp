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
    ///     Every named type declared directly under <paramref name="ns" />, recursing
    ///     into child namespaces but not into nested types. Used only by the two
    ///     walks that recurse into nested types themselves because they emit
    ///     parent-child facts (<c>SymbolDeclarationExtractor</c> and
    ///     <c>SymbolStructuralEdgeExtractor</c>); every other caller uses
    ///     <see cref="GetAllNamedTypes" />.
    /// </summary>
    internal static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers()) yield return type;

        foreach (var childNs in ns.GetNamespaceMembers())
            foreach (var type in GetNamespaceTypeMembers(childNs))
                yield return type;
    }

    /// <summary>
    ///     Every named type under <paramref name="ns" />, nested types included,
    ///     depth-first and in the same namespace order as
    ///     <see cref="GetNamespaceTypeMembers" />.
    /// </summary>
    internal static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in GetNestedTypes(type))
                yield return nested;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
            foreach (var type in GetAllNamedTypes(childNs))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol parent)
    {
        foreach (var nested in parent.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in GetNestedTypes(nested))
                yield return deeper;
        }
    }

    internal static IEnumerable<INamedTypeSymbol> NestedTypeUses(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                foreach (var nested in TypeUses(array.ElementType))
                    yield return nested;
                break;
            // A nested type of a constructed generic type also carries the type arguments of
            // each containing type: Outer<Arg>.Inner names Arg. The containing type itself is
            // not a fact (D135).
            case INamedTypeSymbol named:
                for (var current = named; current != null; current = current.ContainingType)
                    foreach (var argument in current.TypeArguments)
                        foreach (var nested in TypeUses(argument))
                            yield return nested;
                break;
            // B58: a pointer and a function pointer name the types they point at or
            // carry in their signature.
            case IPointerTypeSymbol pointer:
                foreach (var nested in TypeUses(pointer.PointedAtType))
                    yield return nested;
                break;
            case IFunctionPointerTypeSymbol functionPointer:
                foreach (var nested in TypeUses(functionPointer.Signature.ReturnType))
                    yield return nested;
                foreach (var parameter in functionPointer.Signature.Parameters)
                    foreach (var nested in TypeUses(parameter.Type))
                        yield return nested;
                break;
        }
    }

    internal static IEnumerable<INamedTypeSymbol> TypeUses(ITypeSymbol type)
    {
        // `void` names no type; ReturnsEdgeExtractor and AspNetCoreAdapter skip it too.
        if (type.SpecialType == SpecialType.System_Void)
            yield break;

        if (type is INamedTypeSymbol named && named.TypeKind != TypeKind.Error && !named.IsAnonymousType)
            yield return named.OriginalDefinition;

        foreach (var nested in NestedTypeUses(type))
            yield return nested;
    }

    /// <summary>
    ///     D38: true when the structural extractors write the facts of <paramref name="member" />.
    ///     A written member has them. A compiler-made member has them when Lurp stores it, that is,
    ///     when Roslyn gives it declaring syntax: the synthesized members of a record, the accessors
    ///     of a positional record property and the members of a delegate. A compiler-made member
    ///     without syntax (an implicit constructor, a record copy constructor and EqualityContract,
    ///     a backing field, the accessors of a field-like event) is not stored and has no fact. The
    ///     implementation of a C# 14 extension block member has no fact of its own: its block member
    ///     writes the facts under the same symbol id (SymbolIdFactory).
    /// </summary>
    internal static bool HasStructuralFacts(ISymbol member)
    {
        return !member.IsImplicitlyDeclared ||
               (!member.DeclaringSyntaxReferences.IsEmpty && !IsExtensionImplementation(member));
    }

    /// <summary>
    ///     The member that <paramref name="member" /> overrides (a method, a property or an event),
    ///     or null when it overrides none.
    /// </summary>
    internal static ISymbol? OverriddenMember(ISymbol member)
    {
        return member switch
        {
            IMethodSymbol { IsOverride: true } method => method.OverriddenMethod,
            IPropertySymbol { IsOverride: true } property => property.OverriddenProperty,
            IEventSymbol { IsOverride: true } eventSymbol => eventSymbol.OverriddenEvent,
            _ => null
        };
    }

    /// <summary>
    ///     D15, D22: a dispatch point, that is, a member whose run-time target depends on the
    ///     run-time type: a virtual, abstract or overriding member (an interface member included)
    ///     that is not sealed and whose type is not sealed. A struct, a delegate and a sealed class
    ///     have no derived type, so their members do not dispatch.
    /// </summary>
    internal static bool IsDispatchPoint(ISymbol member)
    {
        return (member.IsVirtual || member.IsAbstract || member.IsOverride) &&
               !member.IsSealed &&
               member.ContainingType is { IsSealed: false };
    }

    private static bool IsExtensionImplementation(ISymbol member)
    {
        return member is IMethodSymbol { IsStatic: true, ContainingType: { } outer } method &&
               outer.GetTypeMembers().Any(block => block.IsExtension &&
                   block.GetMembers().OfType<IMethodSymbol>().Any(blockMember =>
                       SymbolEqualityComparer.Default.Equals(blockMember.AssociatedExtensionImplementation, method)));
    }

    /// <summary>
    ///     D8, D23: the symbol that owns the signature facts (<c>Returns</c> and the parameter and
    ///     return-type <c>References</c>) of <paramref name="method" />: the method, or the delegate
    ///     type for the <c>Invoke</c> of a delegate. The other members of a delegate (constructor,
    ///     <c>BeginInvoke</c>, <c>EndInvoke</c>) own no signature fact: the source types of their
    ///     signatures are the <c>Invoke</c> types, which the delegate type owns.
    /// </summary>
    internal static ISymbol? SignatureOwner(IMethodSymbol method)
    {
        if (method.ContainingType is not { TypeKind: TypeKind.Delegate } delegateType)
            return method;
        return method.MethodKind == MethodKind.DelegateInvoke ? delegateType : null;
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
    ///     The full metadata name of <paramref name="type" />, as the runtime names it: the namespace,
    ///     then the containing types joined by <c>+</c>, each with its arity suffix
    ///     (<c>N.Outer`1+Inner</c>).
    /// </summary>
    internal static string FullMetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } containingType)
            return FullMetadataName(containingType) + "+" + type.MetadataName;
        return type.ContainingNamespace is { IsGlobalNamespace: false } ns
            ? ns.ToDisplayString() + "." + type.MetadataName
            : type.MetadataName;
    }

    /// <summary>
    ///     True when <paramref name="symbol" /> is declared in an assembly other than the
    ///     compilation's own. A symbol with no containing assembly is not outside it.
    /// </summary>
    internal static bool IsOutsideCompilation(ISymbol symbol, Compilation compilation)
    {
        return symbol.ContainingAssembly != null
               && !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly);
    }

    /// <summary>
    ///     Every method-like declaration (methods, constructors, property and event accessors, operators,
    ///     conversion operators, destructors) owned by the given types, paired with its
    ///     syntax node. Declarations whose declaring syntax tree is out of scope
    ///     (<paramref name="inScope" />) are skipped. A partial method is yielded for both
    ///     its definition and its implementation part.
    /// </summary>
    internal static IEnumerable<(IMethodSymbol method, CSharpSyntaxNode syntax)> EnumerateMethodDeclarations(
        IEnumerable<INamedTypeSymbol> types,
        Func<SyntaxTree, bool> inScope)
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
                            if (!inScope(syntaxRef.SyntaxTree))
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
                        foreach (var accessor in AccessorDeclarations([property.GetMethod, property.SetMethod], inScope))
                            yield return accessor;

                        // The getter's declaring syntax for an expression-bodied property
                        // or indexer is the ArrowExpressionClause, which is not a method
                        // body root, so the getter is paired with the property or indexer
                        // declaration here.
                        if (property.GetMethod is { } getter)
                        {
                            foreach (var syntaxRef in property.DeclaringSyntaxReferences)
                            {
                                if (!inScope(syntaxRef.SyntaxTree))
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
                    case IEventSymbol eventSymbol:
                        foreach (var accessor in AccessorDeclarations([eventSymbol.AddMethod, eventSymbol.RemoveMethod], inScope))
                            yield return accessor;

                        break;
                }
            }
    }

    /// <summary>
    ///     The accessors among <paramref name="accessors" /> that have an accessor declaration in
    ///     scope. The accessors of a field-like event have none.
    /// </summary>
    private static IEnumerable<(IMethodSymbol method, CSharpSyntaxNode syntax)> AccessorDeclarations(
        IEnumerable<IMethodSymbol?> accessors,
        Func<SyntaxTree, bool> inScope)
    {
        foreach (var accessor in accessors)
        {
            if (accessor == null)
                continue;

            foreach (var syntaxRef in accessor.DeclaringSyntaxReferences)
            {
                if (!inScope(syntaxRef.SyntaxTree))
                    continue;
                if (syntaxRef.GetSyntax() is AccessorDeclarationSyntax accessorDeclaration)
                    yield return (accessor, accessorDeclaration);
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
}