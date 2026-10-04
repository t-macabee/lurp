using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
///     Extracts edges the syntax-switch member extractors do not cover: the
///     executable roots they never enumerate (field/property initializers,
///     constructor initializers, primary-constructor base arguments, top-level
///     statements, operator/conversion/destructor bodies) and the compiler-lowered
///     call shapes inside every root (method groups, event subscriptions,
///     user-defined operators and conversions, and the implicit invocations behind
///     await, foreach, using, deconstruction, collection initializers and
///     interpolated-string handlers). It walks Roslyn operations; the existing
///     extractors remain the source of the syntax-level facts, and EdgeMerge
///     collapses the overlap by (source, target, kind).
/// </summary>
internal sealed class OperationShapeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string Source, string Target, string Kind)>();

        ExtractAttributeConstructions(edges, seen);

        foreach (var (owner, node) in EnumerateRoots())
        {
            var ownerId = context.MakeSymbolId(owner);
            if (ownerId == null)
                continue;

            var model = context.GetOrCreateSemanticModel(node.SyntaxTree);

            if (node is ConstructorInitializerSyntax ctorInitializer &&
                model.GetSymbolInfo(ctorInitializer).Symbol is IMethodSymbol initializerTarget)
                AddCall(ownerId, initializerTarget, ctorInitializer, model, edges, seen);

            if (node is PrimaryConstructorBaseTypeSyntax primaryBase)
                AddPrimaryBaseConstructor(owner, ownerId, primaryBase, model, edges, seen);


            var operation = model.GetOperation(node);
            if (operation != null)
            {
                Walk(operation, ownerId, model, edges, seen);
                continue;
            }

            // Some nodes do not expose a single root operation on every Roslyn
            // version (e.g. a primary-constructor base list). Walk the operations
            // of the descendant expressions individually; the dedup set absorbs
            // the overlap between nested operations.
            foreach (var child in node.DescendantNodesAndSelf())
            {
                var childOperation = model.GetOperation(child);
                if (childOperation != null)
                    Walk(childOperation, ownerId, model, edges, seen);
            }
        }

        return edges;
    }

    private IEnumerable<(ISymbol Owner, SyntaxNode Node)> EnumerateRoots()
    {
        foreach (var (method, syntax) in context.EnumerateMethodDeclarations())
        {
            var node = syntax switch
            {
                PropertyDeclarationSyntax { ExpressionBody: { } body } => body.Expression,
                IndexerDeclarationSyntax { ExpressionBody: { } body } => body.Expression,
                _ => (SyntaxNode)syntax
            };
            yield return (method, node);
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
                        foreach (var syntaxRef in field.DeclaringSyntaxReferences)
                        {
                            if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                                continue;
                            if (syntaxRef.GetSyntax() is VariableDeclaratorSyntax { Initializer: { } initializer })
                                yield return (field, initializer.Value);
                        }

                        break;
                    case IPropertySymbol property:
                        foreach (var syntaxRef in property.DeclaringSyntaxReferences)
                        {
                            if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                                continue;
                            if (syntaxRef.GetSyntax() is PropertyDeclarationSyntax { Initializer: { } initializer })
                                yield return (property, initializer.Value);
                        }

                        break;
                    case IMethodSymbol method when method.MethodKind == MethodKind.Constructor:
                        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                        {
                            if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                                continue;
                            if (syntaxRef.GetSyntax() is ConstructorDeclarationSyntax { Initializer: { } initializer })
                                yield return (method, initializer);
                        }

                        break;
                }
            }

            foreach (var syntaxRef in typeSymbol.DeclaringSyntaxReferences)
            {
                if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                    continue;
                if (syntaxRef.GetSyntax() is not TypeDeclarationSyntax typeDeclaration)
                    continue;
                if (typeDeclaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault() is not { } primaryBase)
                    continue;

                var constructor = typeSymbol.InstanceConstructors.FirstOrDefault(c => !c.IsImplicitlyDeclared)
                                  ?? typeSymbol.InstanceConstructors.FirstOrDefault();
                if (constructor != null)
                    yield return (constructor, primaryBase);
            }
        }

        var entryPoint = context.Compilation.GetEntryPoint(CancellationToken.None);
        if (entryPoint != null)
        {
            foreach (var tree in context.Compilation.SyntaxTrees)
            {
                if (!ExtractionUtils.IsInScope(context.ScopeDocuments, tree))
                    continue;
                foreach (var global in tree.GetRoot().ChildNodes().OfType<GlobalStatementSyntax>())
                    yield return (entryPoint, global);
            }
        }
    }

    private void Walk(
        IOperation root,
        string ownerId,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        foreach (var operation in root.DescendantsAndSelf())
        {
            switch (operation)
            {
                case IInvocationOperation invocation:
                    // The interpolated-string-handler skeleton construction is
                    // compiler-lowered and not part of the mapped fact set; the
                    // AppendLiteral/AppendFormatted calls below are.
                    if (operation.Parent is IInterpolatedStringHandlerCreationOperation)
                        break;
                    AddCall(ownerId, invocation.TargetMethod, operation.Syntax, model, edges, seen);
                    AddStaticReceiverReference(ownerId, operation.Syntax, model, edges, seen);
                    break;

                case IObjectCreationOperation creation:
                    if (operation.Parent is IInterpolatedStringHandlerCreationOperation)
                        break;
                    if (creation.Type is { } createdType)
                        AddConstructs(ownerId, createdType, operation.Syntax, edges, seen);
                    break;

                case IMethodReferenceOperation methodReference:
                    AddMethodGroupRef(ownerId, methodReference.Method, operation.Syntax, edges, seen);
                    break;

                case IInterpolatedStringAppendOperation append when append.AppendCall is IInvocationOperation appendCall:
                    AddCall(ownerId, appendCall.TargetMethod, operation.Syntax, model, edges, seen);
                    break;

                case IAwaitOperation awaitOperation
                    when awaitOperation.Syntax is AwaitExpressionSyntax awaitSyntax:
                    AddAwaitPattern(ownerId, awaitSyntax, model, edges, seen);
                    break;

                case IForEachLoopOperation loopOperation
                    when loopOperation.Syntax is CommonForEachStatementSyntax loopSyntax:
                    AddForeachPattern(ownerId, loopOperation, loopSyntax, model, edges, seen);
                    break;

                case IUsingDeclarationOperation usingDeclaration:
                    AddUsingDispose(ownerId, usingDeclaration.DeclarationGroup, usingDeclaration.IsAsynchronous,
                        operation.Syntax, model, edges, seen);
                    break;

                case IUsingOperation usingOperation:
                    AddUsingDispose(ownerId, usingOperation.Resources, usingOperation.IsAsynchronous,
                        operation.Syntax, model, edges, seen);
                    break;

                case IDeconstructionAssignmentOperation deconstruction
                    when deconstruction.Syntax is AssignmentExpressionSyntax assignmentSyntax:
                    AddDeconstructionCalls(ownerId, deconstruction, assignmentSyntax, model, edges, seen);
                    break;

                case IPropertyReferenceOperation propertyReference:
                    AddMemberAccess(ownerId, propertyReference.Property, propertyReference, edges, seen);
                    break;

                case IFieldReferenceOperation fieldReference:
                    AddMemberAccess(ownerId, fieldReference.Field, fieldReference, edges, seen);
                    break;

                case IUnaryOperation unary when unary.OperatorMethod != null:
                    AddCall(ownerId, unary.OperatorMethod, operation.Syntax, model, edges, seen);
                    break;

                case IBinaryOperation binary when binary.OperatorMethod != null:
                    AddCall(ownerId, binary.OperatorMethod, operation.Syntax, model, edges, seen);
                    break;

                case ICompoundAssignmentOperation compound when compound.OperatorMethod != null:
                    AddCall(ownerId, compound.OperatorMethod, operation.Syntax, model, edges, seen);
                    break;

                case IIncrementOrDecrementOperation increment when increment.OperatorMethod != null:
                    AddCall(ownerId, increment.OperatorMethod, operation.Syntax, model, edges, seen);
                    break;

                case IConversionOperation conversion when conversion.OperatorMethod != null:
                    AddCall(ownerId, conversion.OperatorMethod, operation.Syntax, model, edges, seen);
                    break;
            }
        }
    }

    private void ExtractAttributeConstructions(
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        foreach (var typeSymbol in context.GetAllNamedTypes())
        {
            ExtractAttributeConstructions(typeSymbol, edges, seen);

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is INamedTypeSymbol)
                    continue;
                ExtractAttributeConstructions(member, edges, seen);
            }
        }
    }

    private void ExtractAttributeConstructions(
        ISymbol symbol,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        if (symbol.IsImplicitlyDeclared || !context.IsMemberInScope(symbol))
            return;

        var attributes = symbol.GetAttributes();
        if (attributes.IsEmpty)
            return;

        var sourceId = context.MakeSymbolId(symbol);
        if (sourceId == null)
            return;

        foreach (var attribute in attributes)
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass == null)
                continue;

            var targetId = context.MakeSymbolId(attributeClass);
            if (targetId == null)
                continue;

            var syntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(symbol);
            Emit(edges, seen, sourceId, targetId, nameof(EdgeKind.Constructs), syntax);
        }
    }

    private void AddCall(
        string ownerId,
        IMethodSymbol? method,
        SyntaxNode syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        if (method == null)
            return;

        // Local functions cannot be invoked outside their lexical method and are
        // never declared as snapshot symbols, so a Calls edge to one would orphan
        // out; anonymous-function symbols are compiler constructs, not members.
        if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            return;

        context.RecordFilteredExternal(method, syntax);
        var targetId = context.MakeSymbolId(method);
        if (targetId == null || targetId == ownerId)
            return;

        Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Calls), syntax);
    }

    private void AddMethodGroupRef(
        string ownerId,
        IMethodSymbol? method,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        if (method == null)
            return;
        if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            return;

        context.RecordFilteredExternal(method, syntax);
        var targetId = context.MakeSymbolId(method);
        if (targetId == null)
            return;

        Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.MethodGroupRef), syntax);
    }

    private void AddConstructs(
        string ownerId,
        ITypeSymbol type,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        context.RecordFilteredExternal(type, syntax);
        var targetId = context.MakeSymbolId(type);
        if (targetId == null || targetId == ownerId)
            return;

        Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Constructs), syntax);
    }

    private void AddMemberRead(
        string ownerId,
        IPropertySymbol? property,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        if (property == null)
            return;

        context.RecordFilteredExternal(property, syntax);
        var targetId = context.MakeSymbolId(property);
        if (targetId == null)
            return;

        Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Reads), syntax);
    }

    private void AddMemberAccess(
        string ownerId,
        ISymbol member,
        IOperation operation,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        context.RecordFilteredExternal(member, operation.Syntax);
        var targetId = context.MakeSymbolId(member);
        if (targetId == null)
            return;

        var (reads, writes) = ClassifyAccess(operation);
        if (reads)
            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Reads), operation.Syntax);
        if (writes)
            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Writes), operation.Syntax);
    }

    private static (bool Reads, bool Writes) ClassifyAccess(IOperation operation)
    {
        switch (operation.Parent)
        {
            case IAssignmentOperation assignment when ReferenceEquals(assignment.Target, operation):
                return (false, true);
            case ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, operation):
            case IIncrementOrDecrementOperation increment when ReferenceEquals(increment.Target, operation):
                return (true, true);
            case IArgumentOperation argument when argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out }:
                return (false, true);
            default:
                return (true, false);
        }
    }

    private void AddStaticReceiverReference(
        string ownerId,
        SyntaxNode syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        if (syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess })
            return;
        if (model.GetSymbolInfo(memberAccess.Expression).Symbol is not INamedTypeSymbol containingType)
            return;

        var targetId = context.MakeSymbolId(containingType);
        if (targetId == null || targetId == ownerId)
            return;

        Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.References), memberAccess.Expression);
    }

    private void AddPrimaryBaseConstructor(
        ISymbol owner,
        string ownerId,
        PrimaryConstructorBaseTypeSyntax primaryBase,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        // Roslyn exposes the bound base constructor for a primary-constructor
        // base clause directly; keep the signature match as a fallback for
        // error-state bindings where the symbol lookup returns nothing.
        if (model.GetSymbolInfo(primaryBase).Symbol is IMethodSymbol resolved)
        {
            AddCall(ownerId, resolved, primaryBase, model, edges, seen);
            return;
        }

        if (owner is not IMethodSymbol constructor || constructor.ContainingType?.BaseType is not INamedTypeSymbol baseType)
            return;

        var argumentTypes = primaryBase.ArgumentList.Arguments
            .Select(argument => model.GetTypeInfo(argument.Expression).Type)
            .ToArray();

        var compilation = (CSharpCompilation)context.Compilation;
        foreach (var candidate in baseType.InstanceConstructors)
        {
            if (candidate.Parameters.Length != argumentTypes.Length)
                continue;

            var matches = true;
            for (var i = 0; i < argumentTypes.Length; i++)
            {
                var argumentType = argumentTypes[i];
                if (argumentType == null)
                {
                    matches = false;
                    break;
                }

                var conversion = compilation.ClassifyConversion(argumentType, candidate.Parameters[i].Type);
                if (!conversion.IsImplicit)
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
                continue;

            AddCall(ownerId, candidate, primaryBase, model, edges, seen);
            break;
        }
    }

    private void AddUsingDispose(
        string ownerId,
        IOperation? resources,
        bool isAsync,
        SyntaxNode syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        var resourceTypes = new List<ITypeSymbol>();
        switch (resources)
        {
            case IVariableDeclarationGroupOperation group:
                foreach (var declaration in group.Declarations)
                {
                    if (declaration.Type is { } declaredType)
                        resourceTypes.Add(declaredType);
                    foreach (var declarator in declaration.Declarators)
                    {
                        if (declarator.Initializer?.Value.Type is { } initializerType)
                            resourceTypes.Add(initializerType);
                    }
                }

                break;
            default:
                if (resources?.Type is { } resourceType)
                    resourceTypes.Add(resourceType);
                break;
        }

        foreach (var resourceType in resourceTypes)
        {
            var disposeMethod = ResolveDisposeMethod(resourceType, isAsync);
            if (disposeMethod != null)
                AddCall(ownerId, disposeMethod, syntax, model, edges, seen);
        }
    }

    private IMethodSymbol? ResolveDisposeMethod(ITypeSymbol type, bool isAsync)
    {
        if (type is not INamedTypeSymbol namedType)
            return null;

        var methodName = isAsync ? "DisposeAsync" : "Dispose";

        var direct = namedType.GetMembers(methodName).OfType<IMethodSymbol>()
            .FirstOrDefault(method => method is { IsStatic: false, Parameters.Length: 0 });
        if (direct != null)
            return direct;

        var specialType = isAsync
            ? context.Compilation.GetTypeByMetadataName("System.IAsyncDisposable")
            : context.Compilation.GetSpecialType(SpecialType.System_IDisposable);
        if (specialType is null || specialType.TypeKind == TypeKind.Error)
            return null;

        var interfaceMethod = specialType.GetMembers(methodName).OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 0);
        if (interfaceMethod == null)
            return null;

        return namedType.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol ?? interfaceMethod;
    }

    private void AddAwaitPattern(
        string ownerId,
        AwaitExpressionSyntax syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        // CSharpSemanticModel.GetAwaitExpressionInfo is not public on the pinned
        // Roslyn version, so resolve the await pattern members from the public
        // symbol model. The language rules find GetAwaiter/IsCompleted/GetResult
        // on the awaited type and its awaiter; instance methods cover the shapes
        // in scope (extension-method await patterns are not resolved).
        var awaitedType = model.GetTypeInfo(syntax.Expression).Type;
        if (awaitedType == null)
            return;

        var getAwaiter = FindParameterlessInstanceMethod(awaitedType, "GetAwaiter");
        if (getAwaiter == null)
            return;

        AddCall(ownerId, getAwaiter, syntax, model, edges, seen);

        var awaiterType = getAwaiter.ReturnType;
        AddCall(ownerId, FindParameterlessInstanceMethod(awaiterType, "GetResult"), syntax, model, edges, seen);
        AddMemberRead(ownerId, FindParameterlessProperty(awaiterType, "IsCompleted"), syntax, edges, seen);
    }

    private void AddForeachPattern(
        string ownerId,
        IForEachLoopOperation loop,
        CommonForEachStatementSyntax syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        var collectionType = model.GetTypeInfo(syntax.Expression).Type;
        if (collectionType == null)
            return;

        var isAsync = loop.IsAsynchronous;

        var getEnumerator = FindParameterlessInstanceMethod(collectionType, isAsync ? "GetAsyncEnumerator" : "GetEnumerator");
        if (getEnumerator == null)
        {
            var enumerableInterface = isAsync
                ? FindInterface(collectionType, static i => i.MetadataName == "IAsyncEnumerable`1")
                : FindInterface(collectionType, static i => i.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                  ?? FindInterface(collectionType, static i => i.SpecialType == SpecialType.System_Collections_IEnumerable);
            if (enumerableInterface != null)
                getEnumerator = FindInterfaceMember(collectionType, enumerableInterface, isAsync ? "GetAsyncEnumerator" : "GetEnumerator");
        }

        if (getEnumerator == null)
            return;

        AddCall(ownerId, getEnumerator, syntax, model, edges, seen);

        var enumeratorType = getEnumerator.ReturnType;
        var moveNext = FindParameterlessInstanceMethod(enumeratorType, isAsync ? "MoveNextAsync" : "MoveNext")
                       ?? FindEnumeratorInterfaceMember(enumeratorType, isAsync, "MoveNext") as IMethodSymbol;
        AddCall(ownerId, moveNext, syntax, model, edges, seen);

        var current = FindParameterlessProperty(enumeratorType, "Current")
                      ?? FindEnumeratorInterfaceMember(enumeratorType, isAsync, "Current") as IPropertySymbol;
        AddMemberRead(ownerId, current, syntax, edges, seen);

        AddCall(ownerId, FindParameterlessInstanceMethod(enumeratorType, isAsync ? "DisposeAsync" : "Dispose"),
            syntax, model, edges, seen);
    }

    private void AddDeconstructionCalls(
        string ownerId,
        IDeconstructionAssignmentOperation deconstruction,
        AssignmentExpressionSyntax syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        var valueType = deconstruction.Value.Type;
        if (valueType == null)
            return;

        // One Deconstruct call covers the top-level designation; a declaration
        // form such as 'var (left, right) = x' carries the designation on an
        // IDeclarationExpressionOperation instead of a tuple operation. Nested
        // designations would add further calls, which the fixture corpus does
        // not exercise yet.
        var count = deconstruction.Target switch
        {
            ITupleOperation tuple => tuple.Elements.Length,
            IDeclarationExpressionOperation
            {
                Syntax: DeclarationExpressionSyntax
                {
                    Designation: ParenthesizedVariableDesignationSyntax designation
                }
            } => designation.Variables.Count,
            _ => 0
        };
        if (count == 0)
            return;

        var deconstruct = valueType.GetMembers("Deconstruct").OfType<IMethodSymbol>()
            .FirstOrDefault(method => !method.IsStatic &&
                                      method.Parameters.Length == count &&
                                      method.Parameters.All(parameter => parameter.RefKind == RefKind.Out));
        AddCall(ownerId, deconstruct, syntax, model, edges, seen);
    }

    private static IMethodSymbol? FindParameterlessInstanceMethod(ITypeSymbol? type, string name)
    {
        if (type is not INamedTypeSymbol namedType)
            return null;
        return namedType.GetMembers(name).OfType<IMethodSymbol>()
            .FirstOrDefault(method => !method.IsStatic && method.Parameters.Length == 0);
    }

    private static IPropertySymbol? FindParameterlessProperty(ITypeSymbol? type, string name)
    {
        if (type is not INamedTypeSymbol namedType)
            return null;
        return namedType.GetMembers(name).OfType<IPropertySymbol>()
            .FirstOrDefault(property => !property.IsStatic && property.Parameters.Length == 0);
    }

    private static INamedTypeSymbol? FindInterface(ITypeSymbol type, Func<INamedTypeSymbol, bool> predicate)
    {
        if (type is not INamedTypeSymbol namedType)
            return null;
        if (predicate(namedType))
            return namedType;
        return namedType.AllInterfaces.FirstOrDefault(predicate);
    }

    private static IMethodSymbol? FindInterfaceMember(ITypeSymbol type, INamedTypeSymbol interfaceType, string name)
    {
        var interfaceMethod = interfaceType.GetMembers(name).OfType<IMethodSymbol>()
            .FirstOrDefault(method => !method.IsStatic);
        if (interfaceMethod == null)
            return null;
        if (type is INamedTypeSymbol namedType &&
            namedType.FindImplementationForInterfaceMember(interfaceMethod) is IMethodSymbol implementation)
            return implementation;
        return interfaceMethod;
    }

    private static ISymbol? FindEnumeratorInterfaceMember(ITypeSymbol enumeratorType, bool isAsync, string name)
    {
        var interfaceType = isAsync
            ? FindInterface(enumeratorType, static i => i.MetadataName == "IAsyncEnumerator`1")
            : FindInterface(enumeratorType, static i => i.SpecialType == SpecialType.System_Collections_Generic_IEnumerator_T)
              ?? FindInterface(enumeratorType, static i => i.SpecialType == SpecialType.System_Collections_IEnumerator);
        return interfaceType == null ? null : FindInterfaceMember(enumeratorType, interfaceType, name);
    }

    private void Emit(
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen,
        string sourceId,
        string targetId,
        string kind,
        SyntaxNode? syntax)
    {
        if (!seen.Add((sourceId, targetId, kind)))
            return;

        var location = syntax != null
            ? context.GetLocationInfo(syntax.GetLocation())
            : ((string? path, int? sl, int? sc, int? el, int? ec)?)null;
        edges.Add(context.MakeEdge(sourceId, targetId, kind, ExtractorConstants.OperationShapesExtractor, location));
    }
}
