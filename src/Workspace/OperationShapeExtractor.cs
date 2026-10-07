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
///     await, foreach, using, deconstruction, collection initializers,
///     interpolated-string handlers, and the C# pattern, index/range and
///     collection-expression shapes). Event references are <c>Reads</c> or
///     <c>Writes</c> to the event. Await, foreach and deconstruction read their
///     bound members from Roslyn's binding APIs (<c>GetAwaitExpressionInfo</c>,
///     <c>GetForEachStatementInfo</c> and <c>GetDeconstructionInfo</c>); the pattern
///     shapes read <c>IRecursivePatternOperation</c>, <c>IListPatternOperation</c>,
///     <c>ISlicePatternOperation</c>, <c>IImplicitIndexerReferenceOperation</c> and
///     <c>ICollectionExpressionOperation</c>. It walks Roslyn operations; the
///     existing extractors remain the source of the syntax-level facts, and
///     EdgeMerge collapses the overlap by (source, target, kind).
///     B30 type uses (<c>is</c> and type patterns, explicit casts, <c>default(T)</c>,
///     <c>sizeof</c>, array creation, local declarations, <c>catch</c> types, method and
///     constructor type arguments) are <c>References</c> to the type and to the types
///     nested inside it; <c>typeof</c> is left to <c>ReflectionTypeRef</c>.
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
                AddPrimaryBaseConstructor(ownerId, primaryBase, model, edges, seen);


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
                            else if (syntaxRef.GetSyntax() is EnumMemberDeclarationSyntax { EqualsValue: { } equalsValue })
                                yield return (field, equalsValue.Value);
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

                        if (property.IsIndexer)
                        {
                            foreach (var parameterDefault in EnumerateParameterDefaults(property.Parameters))
                                yield return (property, parameterDefault);
                        }

                        break;
                    case IMethodSymbol method:
                        foreach (var parameterDefault in EnumerateParameterDefaults(method.Parameters))
                            yield return (method, parameterDefault);

                        if (method.MethodKind == MethodKind.Constructor)
                        {
                            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                            {
                                if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                                    continue;
                                if (syntaxRef.GetSyntax() is ConstructorDeclarationSyntax { Initializer: { } initializer })
                                    yield return (method, initializer);
                            }
                        }

                        break;
                    case IEventSymbol eventSymbol:
                        foreach (var syntaxRef in eventSymbol.DeclaringSyntaxReferences)
                        {
                            if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                                continue;
                            if (syntaxRef.GetSyntax() is VariableDeclaratorSyntax { Initializer: { } initializer })
                                yield return (eventSymbol, initializer.Value);
                        }

                        break;
                }
            }

            // R4.5: a delegate parameter default is owned by the delegate type; Roslyn's
            // Invoke method is not a stored symbol, so its ContainingSymbol is not an id.
            if (typeSymbol.TypeKind == TypeKind.Delegate && typeSymbol.DelegateInvokeMethod is { } invoke)
            {
                foreach (var parameterDefault in EnumerateParameterDefaults(invoke.Parameters))
                    yield return (typeSymbol, parameterDefault);
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

    private IEnumerable<SyntaxNode> EnumerateParameterDefaults(IEnumerable<IParameterSymbol> parameters)
    {
        foreach (var parameter in parameters)
        {
            foreach (var syntaxRef in parameter.DeclaringSyntaxReferences)
            {
                if (!ExtractionUtils.IsInScope(context.ScopeDocuments, syntaxRef.SyntaxTree))
                    continue;
                if (syntaxRef.GetSyntax() is ParameterSyntax { Default: { } parameterDefault })
                    yield return parameterDefault.Value;
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
                    foreach (var typeArgument in invocation.TargetMethod.TypeArguments)
                        AddTypeUses(ownerId, typeArgument, operation.Syntax, edges, seen);
                    break;

                case IObjectCreationOperation creation:
                    if (operation.Parent is IInterpolatedStringHandlerCreationOperation)
                        break;
                    if (creation.Type is { } createdType)
                    {
                        AddConstructs(ownerId, createdType, operation.Syntax, edges, seen);
                        foreach (var nestedType in ExtractionUtils.NestedTypeUses(createdType))
                            AddTypeUses(ownerId, nestedType, operation.Syntax, edges, seen);
                    }

                    if (creation.Constructor is { IsImplicitlyDeclared: false } constructor)
                        AddCall(ownerId, constructor, operation.Syntax, model, edges, seen);
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

                case IForEachLoopOperation
                    when operation.Syntax is CommonForEachStatementSyntax loopSyntax:
                    AddForeachPattern(ownerId, loopSyntax, model, edges, seen);
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
                    AddDeconstructionCalls(ownerId, model.GetDeconstructionInfo(assignmentSyntax),
                        assignmentSyntax, model, edges, seen);
                    break;

                case IPropertyReferenceOperation propertyReference:
                    AddMemberAccess(ownerId, propertyReference.Property, propertyReference, edges, seen);
                    break;

                case IFieldReferenceOperation fieldReference:
                    AddMemberAccess(ownerId, fieldReference.Field, fieldReference, edges, seen);
                    break;

                case IEventReferenceOperation eventReference:
                    AddMemberAccess(ownerId, eventReference.Event, operation, edges, seen);
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

                case IConversionOperation conversion:
                    if (conversion.OperatorMethod != null)
                        AddCall(ownerId, conversion.OperatorMethod, operation.Syntax, model, edges, seen);
                    if (!conversion.IsImplicit)
                        AddTypeUses(ownerId, conversion.Type, operation.Syntax, edges, seen);
                    break;

                // B19: implicit calls the syntax switch never sees. Each reads a
                // member Roslyn bound for the shape.
                case IRecursivePatternOperation recursivePattern:
                    AddTypeUses(ownerId, recursivePattern.MatchedType, operation.Syntax, edges, seen);
                    if (recursivePattern.DeconstructSymbol is IMethodSymbol deconstruct)
                        AddCall(ownerId, deconstruct, operation.Syntax, model, edges, seen);
                    break;

                case IListPatternOperation listPattern:
                    AddMemberBySymbolKind(ownerId, listPattern.LengthSymbol, operation.Syntax, model, edges, seen);
                    AddMemberBySymbolKind(ownerId, listPattern.IndexerSymbol, operation.Syntax, model, edges, seen);
                    break;

                case ISlicePatternOperation { SliceSymbol: { } sliceSymbol }:
                    AddMemberBySymbolKind(ownerId, sliceSymbol, operation.Syntax, model, edges, seen);
                    break;

                case IImplicitIndexerReferenceOperation implicitIndexer:
                    AddMemberBySymbolKind(ownerId, implicitIndexer.LengthSymbol, operation.Syntax, model, edges, seen);
                    if (implicitIndexer.IndexerSymbol is IPropertySymbol indexerProperty)
                        AddMemberAccess(ownerId, indexerProperty, implicitIndexer, edges, seen);
                    else
                        AddCall(ownerId, implicitIndexer.IndexerSymbol as IMethodSymbol, operation.Syntax, model, edges, seen);
                    break;

                case ICollectionExpressionOperation { ConstructMethod: { } constructMethod }:
                    if (constructMethod.MethodKind == MethodKind.Constructor)
                    {
                        AddConstructs(ownerId, constructMethod.ContainingType, operation.Syntax, edges, seen);
                        if (!constructMethod.IsImplicitlyDeclared)
                            AddCall(ownerId, constructMethod, operation.Syntax, model, edges, seen);
                    }
                    else
                    {
                        AddCall(ownerId, constructMethod, operation.Syntax, model, edges, seen);
                    }

                    break;

                // B30: every source type named in a body is a References target;
                // typeof is left to ReflectionTypeRef.
                case IIsTypeOperation isType:
                    AddTypeUses(ownerId, isType.TypeOperand, operation.Syntax, edges, seen);
                    break;

                case IDeclarationPatternOperation declarationPattern:
                    AddTypeUses(ownerId, declarationPattern.MatchedType, operation.Syntax, edges, seen);
                    break;

                case ITypePatternOperation typePattern:
                    AddTypeUses(ownerId, typePattern.MatchedType, operation.Syntax, edges, seen);
                    break;

                case ISizeOfOperation sizeOf:
                    AddTypeUses(ownerId, sizeOf.TypeOperand, operation.Syntax, edges, seen);
                    break;

                case IArrayCreationOperation arrayCreation:
                    AddTypeUses(ownerId, arrayCreation.Type, operation.Syntax, edges, seen);
                    break;

                case IVariableDeclaratorOperation declarator:
                    AddTypeUses(ownerId, declarator.Symbol.Type, operation.Syntax, edges, seen);
                    break;

                case ICatchClauseOperation catchClause:
                    AddTypeUses(ownerId, catchClause.ExceptionType, operation.Syntax, edges, seen);
                    break;

                case IDefaultValueOperation defaultValue when defaultValue.Syntax is DefaultExpressionSyntax:
                    AddTypeUses(ownerId, defaultValue.Type, operation.Syntax, edges, seen);
                    break;
            }
        }
    }

    private void AddTypeUses(
        string ownerId,
        ITypeSymbol? type,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        if (type == null)
            return;

        foreach (var used in ExtractionUtils.TypeUses(type))
        {
            context.RecordFilteredExternal(used, syntax);
            var targetId = context.MakeSymbolId(used);
            if (targetId == null || targetId == ownerId)
                continue;

            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.References), syntax);
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

        var sourceId = context.MakeSymbolId(symbol);
        if (sourceId == null)
            return;

        var syntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(symbol);
        ExtractAttributes(symbol.GetAttributes(), sourceId, syntax, edges, seen);

        // B33: parameter and return-value attributes belong to the declaring
        // member (an indexer parameter to the indexer).
        switch (symbol)
        {
            case IMethodSymbol method:
                foreach (var parameter in method.Parameters)
                    ExtractAttributes(parameter.GetAttributes(), sourceId, syntax, edges, seen);
                ExtractAttributes(method.GetReturnTypeAttributes(), sourceId, syntax, edges, seen);
                break;
            case IPropertySymbol { IsIndexer: true } indexer:
                foreach (var parameter in indexer.Parameters)
                    ExtractAttributes(parameter.GetAttributes(), sourceId, syntax, edges, seen);
                break;
        }
    }

    private void ExtractAttributes(
        IEnumerable<AttributeData> attributes,
        string sourceId,
        SyntaxNode? syntax,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        foreach (var attribute in attributes)
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass == null)
                continue;

            var targetId = context.MakeSymbolId(attributeClass);
            if (targetId == null)
                continue;

            Emit(edges, seen, sourceId, targetId, nameof(EdgeKind.Constructs), syntax);

            // B28: an attribute application calls its constructor when the
            // constructor is declared in source.
            if (attribute.AttributeConstructor is { IsImplicitlyDeclared: false } attributeConstructor)
            {
                context.RecordFilteredExternal(attributeConstructor, syntax);
                var constructorId = context.MakeSymbolId(attributeConstructor);
                if (constructorId != null)
                    Emit(edges, seen, sourceId, constructorId, nameof(EdgeKind.Calls), syntax);
            }

            // B33: an attribute argument is a root; walk its expression.
            if (attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax { ArgumentList: { } argumentList } &&
                ExtractionUtils.IsInScope(context.ScopeDocuments, argumentList.SyntaxTree))
            {
                var model = context.GetOrCreateSemanticModel(argumentList.SyntaxTree);
                foreach (var argument in argumentList.Arguments)
                {
                    if (model.GetOperation(argument.Expression) is { } argumentOperation)
                        Walk(argumentOperation, sourceId, model, edges, seen);
                }
            }
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
            case IEventAssignmentOperation eventAssignment when ReferenceEquals(eventAssignment.EventReference, operation):
                return (false, true);
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
        string ownerId,
        PrimaryConstructorBaseTypeSyntax primaryBase,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        // Roslyn exposes the bound base constructor for a primary-constructor base
        // clause directly. When the binding fails (error-state code), emit no edge
        // and record the unresolved binding instead of guessing a target.
        var symbolInfo = model.GetSymbolInfo(primaryBase);
        if (symbolInfo.Symbol is not IMethodSymbol resolved)
        {
            context.RecordUnresolvedBinding(symbolInfo, primaryBase, model);
            return;
        }

        AddCall(ownerId, resolved, primaryBase, model, edges, seen);
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
        var info = model.GetAwaitExpressionInfo(syntax);
        AddCall(ownerId, info.GetAwaiterMethod, syntax, model, edges, seen);
        AddCall(ownerId, info.GetResultMethod, syntax, model, edges, seen);
        AddMemberRead(ownerId, info.IsCompletedProperty, syntax, edges, seen);
    }

    private void AddForeachPattern(
        string ownerId,
        CommonForEachStatementSyntax syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        var info = model.GetForEachStatementInfo(syntax);
        AddCall(ownerId, info.GetEnumeratorMethod, syntax, model, edges, seen);
        AddCall(ownerId, info.MoveNextMethod, syntax, model, edges, seen);
        AddMemberRead(ownerId, info.CurrentProperty, syntax, edges, seen);
        AddCall(ownerId, info.DisposeMethod, syntax, model, edges, seen);

        // A foreach deconstruction ('foreach (var (a, b) in pairs)') has its own
        // Deconstruct calls, reached through the same Nested recursion as an
        // assignment deconstruction.
        if (syntax is ForEachVariableStatementSyntax variableSyntax)
            AddDeconstructionCalls(ownerId, model.GetDeconstructionInfo(variableSyntax), variableSyntax,
                model, edges, seen);
    }

    private void AddDeconstructionCalls(
        string ownerId,
        DeconstructionInfo info,
        SyntaxNode syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        AddCall(ownerId, info.Method, syntax, model, edges, seen);
        foreach (var nested in info.Nested)
            AddDeconstructionCalls(ownerId, nested, syntax, model, edges, seen);
    }

    private void AddMemberBySymbolKind(
        string ownerId,
        ISymbol? member,
        SyntaxNode syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        HashSet<(string Source, string Target, string Kind)> seen)
    {
        switch (member)
        {
            case IPropertySymbol property:
                AddMemberRead(ownerId, property, syntax, edges, seen);
                break;
            case IMethodSymbol method:
                AddCall(ownerId, method, syntax, model, edges, seen);
                break;
        }
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
