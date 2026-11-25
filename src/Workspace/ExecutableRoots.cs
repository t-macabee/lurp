using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

/// <summary>
///     D8, D20: the one owner rule. Each root is a piece of code that runs, paired with the member
///     that runs it: a method, accessor, operator, conversion or destructor body (the accessor owns
///     an expression-bodied property or indexer), a field, property, enum member or event
///     initializer (each variable owns its own initializer), a parameter default (the method; an
///     indexer parameter: the indexer; a delegate parameter: the delegate type), a constructor
///     initializer, the arguments of a primary-constructor base list (the primary constructor) and
///     the top-level statements (the entry point). A local function, a lambda and a query clause
///     are inside the root of the member that contains them. An attribute application is owned by
///     the attributed member (a type attribute: the type; a parameter or return-value attribute:
///     its method or indexer). Every extractor that names the source of a body fact reads these
///     roots, so one body has one owner for every edge kind.
/// </summary>
internal static class ExecutableRoots
{
    /// <summary>The code roots in scope, each with its owner (see the class summary).</summary>
    internal static IEnumerable<(ISymbol Owner, SyntaxNode Node)> Bodies(ExtractionContextBase context)
    {
        foreach (var (method, syntax) in ExtractionUtils.EnumerateMethodDeclarations(context.GetAllNamedTypes(), context.IsInScope))
        {
            foreach (var root in MethodDeclarationRoots(method, syntax))
                yield return root;
        }

        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member.IsImplicitlyDeclared)
                    continue;

                switch (member)
                {
                    case IFieldSymbol field:
                        foreach (var root in FieldRoots(context, field))
                            yield return root;
                        break;
                    case IPropertySymbol property:
                        foreach (var root in PropertyRoots(context, property))
                            yield return root;
                        break;
                    case IMethodSymbol method:
                        foreach (var root in MethodExtraRoots(context, method))
                            yield return root;
                        break;
                    case IEventSymbol eventSymbol:
                        foreach (var root in EventRoots(context, eventSymbol))
                            yield return root;
                        break;
                }
            }

            // R4.5: a delegate parameter default is owned by the delegate type; Roslyn's
            // Invoke method is not a stored symbol, so its ContainingSymbol is not an id.
            foreach (var root in DelegateRoots(context, typeSymbol))
                yield return root;

            if (PrimaryConstructorFor(typeSymbol) is { } primaryCtor)
            {
                foreach (var root in PrimaryConstructorRoots(context, typeSymbol, primaryCtor))
                    yield return root;
            }
        }

        var entryPoint = context.Compilation.GetEntryPoint(CancellationToken.None);
        if (entryPoint != null)
        {
            foreach (var root in EntryPointRoots(context, entryPoint))
                yield return root;
        }
    }

    /// <summary>The code roots in scope owned by a single symbol.</summary>
    internal static IEnumerable<(ISymbol Owner, SyntaxNode Node)> BodiesForOwner(ExtractionContextBase context, ISymbol owner)
    {
        if (owner is IMethodSymbol { ReducedFrom: not null } reducedMethod)
            owner = reducedMethod.ReducedFrom;
        owner = owner.OriginalDefinition;
        if (owner.ContainingType is { IsExtension: true } &&
            owner is IMethodSymbol extMethod &&
            extMethod.AssociatedExtensionImplementation is { } impl)
            owner = impl.OriginalDefinition;

        switch (owner)
        {
            case IMethodSymbol method:
                if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove)
                {
                    foreach (var (accessor, syntax) in ExtractionUtils.AccessorDeclarations([method], context.IsInScope))
                    foreach (var root in MethodDeclarationRoots(accessor, syntax))
                        yield return root;

                    if (method.MethodKind == MethodKind.PropertyGet && method.AssociatedSymbol is IPropertySymbol associatedProperty)
                    {
                        foreach (var (getter, syntax) in ExtractionUtils.EnumerateExpressionBodiedGetter(method, associatedProperty, context.IsInScope))
                        foreach (var root in MethodDeclarationRoots(getter, syntax))
                            yield return root;
                    }
                }
                else
                {
                    foreach (var (part, syntax) in ExtractionUtils.EnumerateMethodDeclarations(method, context.IsInScope))
                    foreach (var root in MethodDeclarationRoots(part, syntax))
                        yield return root;
                }

                if (!method.IsImplicitlyDeclared)
                {
                    foreach (var root in MethodExtraRoots(context, method))
                        yield return root;
                }

                if (method.MethodKind == MethodKind.Constructor &&
                    method.ContainingType is { } containingType &&
                    SymbolEqualityComparer.Default.Equals(method, PrimaryConstructorFor(containingType)))
                {
                    foreach (var root in PrimaryConstructorRoots(context, containingType, method))
                        yield return root;
                }

                var entryPoint = context.Compilation.GetEntryPoint(CancellationToken.None);
                if (entryPoint != null && SymbolEqualityComparer.Default.Equals(method, entryPoint))
                {
                    foreach (var root in EntryPointRoots(context, entryPoint))
                        yield return root;
                }

                break;

            case IFieldSymbol field when !field.IsImplicitlyDeclared:
                foreach (var root in FieldRoots(context, field))
                    yield return root;
                break;

            case IPropertySymbol property when !property.IsImplicitlyDeclared:
                foreach (var root in PropertyRoots(context, property))
                    yield return root;
                break;

            case IEventSymbol eventSymbol when !eventSymbol.IsImplicitlyDeclared:
                foreach (var root in EventRoots(context, eventSymbol))
                    yield return root;
                break;

            case INamedTypeSymbol typeSymbol:
                foreach (var root in DelegateRoots(context, typeSymbol))
                    yield return root;
                break;
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> MethodDeclarationRoots(IMethodSymbol method, CSharpSyntaxNode syntax)
    {
        var node = syntax switch
        {
            PropertyDeclarationSyntax { ExpressionBody: { } body } => body.Expression,
            IndexerDeclarationSyntax { ExpressionBody: { } body } => body.Expression,
            _ => (SyntaxNode)syntax
        };
        yield return (method, node);

        // B33: a local function's or lambda's parameter default sits inside a
        // method body but not in the body's operation tree; its owner is the
        // enclosing member.
        foreach (var parameter in syntax.DescendantNodes().OfType<ParameterSyntax>())
        {
            if (parameter.Default is not { } parameterDefault)
                continue;
            if (parameter.Parent?.Parent is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                continue;
            yield return (method, parameterDefault.Value);
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> FieldRoots(ExtractionContextBase context, IFieldSymbol field)
    {
        foreach (var syntax in InScopeDeclarations(context, field))
        {
            if (syntax is VariableDeclaratorSyntax { Initializer: { } initializer })
                yield return (field, initializer.Value);
            else if (syntax is EnumMemberDeclarationSyntax { EqualsValue: { } equalsValue })
                yield return (field, equalsValue.Value);
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> PropertyRoots(ExtractionContextBase context, IPropertySymbol property)
    {
        foreach (var syntax in InScopeDeclarations(context, property))
        {
            if (syntax is PropertyDeclarationSyntax { Initializer: { } initializer })
                yield return (property, initializer.Value);
        }

        if (property.IsIndexer)
        {
            foreach (var parameterDefault in ParameterDefaults(context, property.Parameters))
                yield return (property, parameterDefault);
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> MethodExtraRoots(ExtractionContextBase context, IMethodSymbol method)
    {
        foreach (var parameterDefault in ParameterDefaults(context, method.Parameters))
            yield return (method, parameterDefault);

        if (method.MethodKind == MethodKind.Constructor)
        {
            foreach (var syntax in InScopeDeclarations(context, method))
            {
                if (syntax is ConstructorDeclarationSyntax { Initializer: { } initializer })
                    yield return (method, initializer);
            }
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> EventRoots(ExtractionContextBase context, IEventSymbol eventSymbol)
    {
        foreach (var syntax in InScopeDeclarations(context, eventSymbol))
        {
            if (syntax is VariableDeclaratorSyntax { Initializer: { } initializer })
                yield return (eventSymbol, initializer.Value);
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> DelegateRoots(ExtractionContextBase context, INamedTypeSymbol typeSymbol)
    {
        if (typeSymbol.TypeKind == TypeKind.Delegate && typeSymbol.DelegateInvokeMethod is { } invoke)
        {
            foreach (var parameterDefault in ParameterDefaults(context, invoke.Parameters))
                yield return (typeSymbol, parameterDefault);
        }
    }

    private static IMethodSymbol? PrimaryConstructorFor(INamedTypeSymbol typeSymbol) =>
        typeSymbol.InstanceConstructors.FirstOrDefault(c => !c.IsImplicitlyDeclared)
        ?? typeSymbol.InstanceConstructors.FirstOrDefault();

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> PrimaryConstructorRoots(
        ExtractionContextBase context,
        INamedTypeSymbol typeSymbol,
        IMethodSymbol constructor)
    {
        foreach (var syntax in InScopeDeclarations(context, typeSymbol))
        {
            if (syntax is not TypeDeclarationSyntax typeDeclaration)
                continue;
            if (typeDeclaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault() is not { } primaryBase)
                continue;
            yield return (constructor, primaryBase);
        }
    }

    private static IEnumerable<(ISymbol Owner, SyntaxNode Node)> EntryPointRoots(ExtractionContextBase context, IMethodSymbol entryPoint)
    {
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            if (!context.IsInScope(tree))
                continue;
            foreach (var global in tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>())
                yield return (entryPoint, global);
        }
    }

    /// <summary>
    ///     The operation trees of a root: the operation of the root node, or, when Roslyn gives that
    ///     node none (a primary-constructor base list), the operation of each outermost descendant
    ///     that has one. The trees do not overlap.
    /// </summary>
    internal static IEnumerable<IOperation> Operations(SyntaxNode root, SemanticModel model)
    {
        if (model.GetOperation(root) is { } operation)
        {
            yield return operation;
            yield break;
        }

        foreach (var child in root.ChildNodes())
        {
            foreach (var childOperation in Operations(child, model))
                yield return childOperation;
        }
    }

    /// <summary>
    ///     The attribute applications of every written member in scope, each with its owner: the
    ///     attributes of each type, then of each of its members; a method also owns the attributes
    ///     of its parameters and of its return value, an indexer those of its parameters (B33).
    /// </summary>
    internal static IEnumerable<(ISymbol Owner, AttributeData Attribute)> Attributes(ExtractionContextBase context)
    {
        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            foreach (var attribute in AttributesOf(context, typeSymbol))
                yield return attribute;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol)
                    continue;
                foreach (var attribute in AttributesOf(context, member))
                    yield return attribute;
            }
        }
    }

    private static IEnumerable<(ISymbol Owner, AttributeData Attribute)> AttributesOf(ExtractionContextBase context, ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared || !context.IsMemberInScope(symbol))
            yield break;

        foreach (var attribute in symbol.GetAttributes())
            yield return (symbol, attribute);

        switch (symbol)
        {
            case IMethodSymbol method:
                foreach (var parameter in method.Parameters)
                foreach (var attribute in parameter.GetAttributes())
                    yield return (symbol, attribute);
                foreach (var attribute in method.GetReturnTypeAttributes())
                    yield return (symbol, attribute);
                break;
            case IPropertySymbol { IsIndexer: true } indexer:
                foreach (var parameter in indexer.Parameters)
                foreach (var attribute in parameter.GetAttributes())
                    yield return (symbol, attribute);
                break;
        }
    }

    /// <summary>The declaring syntax of <paramref name="symbol" /> that lies in the extraction scope.</summary>
    private static IEnumerable<SyntaxNode> InScopeDeclarations(ExtractionContextBase context, ISymbol symbol)
    {
        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            if (context.IsInScope(syntaxRef.SyntaxTree))
                yield return syntaxRef.GetSyntax();
        }
    }

    private static IEnumerable<SyntaxNode> ParameterDefaults(ExtractionContextBase context, IEnumerable<IParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
        {
            foreach (var syntax in InScopeDeclarations(context, parameter))
            {
                if (syntax is ParameterSyntax { Default: { } parameterDefault })
                    yield return parameterDefault.Value;
            }
        }
    }
}
