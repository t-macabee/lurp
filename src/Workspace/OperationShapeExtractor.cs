using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

/// <summary>
///     The one member-body pass (S01-F22): it walks the Roslyn operations of every
///     executable root (method, accessor, operator, conversion and destructor bodies,
///     field/property/event initializers, constructor initializers, primary-constructor
///     base arguments, parameter defaults, attribute arguments and top-level statements)
///     and writes <c>Calls</c>, <c>Constructs</c>, <c>Reads</c>, <c>Writes</c>,
///     <c>MethodGroupRef</c>, <c>ExtensionReceiver</c>, <c>Throws</c> and body <c>References</c>, with
///     receiver-type constraints on <c>Calls</c>. D22: an access whose target can dispatch at run
///     time also gets the <c>StaticallyCalls</c> marker. It covers the explicit call shapes and
///     the compiler-lowered ones (method groups, event subscriptions, user-defined
///     operators and conversions, and the implicit invocations behind await, foreach,
///     using, deconstruction, collection initializers, interpolated-string handlers, and
///     the C# pattern, index/range and collection-expression shapes). Event references
///     are <c>Reads</c> or <c>Writes</c> to the event. Await, foreach and deconstruction
///     read their bound members from Roslyn's binding APIs (<c>GetAwaitExpressionInfo</c>,
///     <c>GetForEachStatementInfo</c> and <c>GetDeconstructionInfo</c>). An operation that
///     does not bind (<c>IInvalidOperation</c>) or binds late (<c>dynamic</c>) is recorded
///     as binding incompleteness.
///     B30 type uses (<c>is</c> and type patterns, explicit casts, <c>default(T)</c>,
///     <c>sizeof</c>, array creation, local declarations, <c>catch</c> types, method and
///     constructor type arguments) are <c>References</c> to the type and to the types
///     nested inside it; <c>typeof</c> is left to <c>ReflectionTypeRef</c>.
///     The roots and their owners come from <see cref="ExecutableRoots" /> (D8, D20).
/// </summary>
internal sealed class OperationShapeExtractor(MemberEdgeExtractionContext context) : IMemberEdgeExtractor
{
    List<EdgeRecord> IMemberEdgeExtractor.Extract()
    {
        var edges = new List<EdgeRecord>();
        var seen = new Dictionary<(string Source, string Target, string Kind), int>();

        ExtractAttributeConstructions(edges, seen);

        foreach (var (owner, node) in ExecutableRoots.Bodies(context))
        {
            var ownerId = context.MakeSymbolId(owner);
            if (ownerId == null)
                continue;

            var model = context.GetOrCreateSemanticModel(node.SyntaxTree);

            if (node is ConstructorInitializerSyntax ctorInitializer &&
                model.GetSymbolInfo(ctorInitializer).Symbol is IMethodSymbol initializerTarget)
                AddCall(ownerId, initializerTarget, ctorInitializer, edges, seen);

            if (node is PrimaryConstructorBaseTypeSyntax primaryBase)
                AddPrimaryBaseConstructor(ownerId, primaryBase, model, edges, seen);


            var operation = model.GetOperation(node);
            if (operation != null)
            {
                Walk(operation, owner, ownerId, model, edges, seen);
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
                    Walk(childOperation, owner, ownerId, model, edges, seen);
            }
        }

        return edges;
    }

    private void Walk(
        IOperation root,
        ISymbol owner,
        string ownerId,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        foreach (var operation in root.DescendantsAndSelf())
        {
            switch (operation)
            {
                case IInvocationOperation invocation:
                    AddInvocation(owner, ownerId, invocation, model, edges, seen);
                    break;

                // A binding that failed (missing member, overload resolution failure)
                // or that binds at run time (dynamic) has no target the graph can name.
                // A nameof argument is a compile-time name, not a binding.
                case IInvalidOperation when !NameOfReflectionExtractor.IsInsideNameOf(operation.Syntax):
                case IDynamicInvocationOperation:
                case IDynamicIndexerAccessOperation:
                case IDynamicMemberReferenceOperation when operation.Parent is not IDynamicInvocationOperation:
                    context.RecordUnresolvedBinding(model.GetSymbolInfo(operation.Syntax), operation.Syntax, model);
                    break;

                // C#: a dynamic object creation fixes the created type at compile time; only the
                // constructor binds at run time, so the call is recorded as binding incompleteness.
                case IDynamicObjectCreationOperation dynamicCreation:
                    if (dynamicCreation.Type is { } dynamicType)
                    {
                        AddConstructs(ownerId, dynamicType, operation.Syntax, edges, seen);
                        foreach (var nestedType in ExtractionUtils.NestedTypeUses(dynamicType))
                            AddTypeUses(ownerId, nestedType, operation.Syntax, edges, seen);
                    }

                    context.RecordUnresolvedBinding(model.GetSymbolInfo(operation.Syntax), operation.Syntax, model);
                    break;

                // D35: an interpolated-string handler creation is an object creation like any other.
                case IObjectCreationOperation creation:
                    if (creation.Type is { } createdType)
                    {
                        AddConstructs(ownerId, createdType, operation.Syntax, edges, seen);
                        foreach (var nestedType in ExtractionUtils.NestedTypeUses(createdType))
                            AddTypeUses(ownerId, nestedType, operation.Syntax, edges, seen);
                    }

                    if (creation.Constructor is { IsImplicitlyDeclared: false } constructor)
                        AddCall(ownerId, constructor, operation.Syntax, edges, seen);
                    break;

                case IThrowOperation { Exception: { } thrown }:
                    AddThrows(ownerId, thrown, operation.Syntax, edges, seen);
                    break;

                // D125: a 'with' expression calls the record clone method.
                case IWithOperation { CloneMethod: { } cloneMethod }:
                    AddCall(ownerId, cloneMethod, operation.Syntax, edges, seen);
                    break;

                case IMethodReferenceOperation methodReference:
                    AddMethodGroupRef(ownerId, methodReference.Method, operation.Syntax, edges, seen);
                    AddStaticReceiverReference(ownerId, operation, methodReference.Method, model, edges, seen);
                    break;

                case IInterpolatedStringAppendOperation append when append.AppendCall is IInvocationOperation appendCall:
                    AddCall(ownerId, appendCall.TargetMethod, operation.Syntax, edges, seen);
                    break;

                case IAwaitOperation awaitOperation
                    when awaitOperation.Syntax is AwaitExpressionSyntax awaitSyntax:
                    AddAwaitPattern(ownerId, awaitSyntax, model, edges, seen);
                    break;

                case IForEachLoopOperation
                    when operation.Syntax is CommonForEachStatementSyntax loopSyntax:
                    AddForeachPattern(ownerId, loopSyntax, model, edges, seen);
                    break;

                case ISpreadOperation when operation.Syntax is SpreadElementSyntax spreadSyntax:
                    if (SpeculativePatterns.ForEach(model, spreadSyntax.Expression) is { } spreadInfo)
                        AddEnumerationPattern(ownerId, spreadInfo, operation.Syntax, edges, seen);
                    break;

                case IUsingDeclarationOperation usingDeclaration:
                    AddUsingDispose(ownerId, usingDeclaration.DeclarationGroup, usingDeclaration.IsAsynchronous,
                        operation.Syntax, edges, seen);
                    break;

                case IUsingOperation usingOperation:
                    AddUsingDispose(ownerId, usingOperation.Resources, usingOperation.IsAsynchronous,
                        operation.Syntax, edges, seen);
                    break;

                case IDeconstructionAssignmentOperation deconstruction
                    when deconstruction.Syntax is AssignmentExpressionSyntax assignmentSyntax:
                    AddDeconstructionCalls(ownerId, model.GetDeconstructionInfo(assignmentSyntax),
                        assignmentSyntax, edges, seen);
                    break;

                case IPropertyReferenceOperation propertyReference:
                    AddMemberAccess(owner, ownerId, propertyReference.Property, propertyReference, edges, seen);
                    AddStaticReceiverReference(ownerId, operation, propertyReference.Property, model, edges, seen);
                    break;

                case IFieldReferenceOperation fieldReference:
                    AddMemberAccess(owner, ownerId, fieldReference.Field, fieldReference, edges, seen);
                    AddStaticReceiverReference(ownerId, operation, fieldReference.Field, model, edges, seen);
                    break;

                case IEventReferenceOperation eventReference:
                    AddMemberAccess(owner, ownerId, eventReference.Event, operation, edges, seen);
                    AddStaticReceiverReference(ownerId, operation, eventReference.Event, model, edges, seen);
                    break;

                case IUnaryOperation unary when unary.OperatorMethod != null:
                    AddCall(ownerId, unary.OperatorMethod, operation.Syntax, edges, seen);
                    break;

                case IBinaryOperation binary when binary.OperatorMethod != null:
                    AddCall(ownerId, binary.OperatorMethod, operation.Syntax, edges, seen);
                    AddCall(ownerId, ShortCircuitOperator(binary), operation.Syntax, edges, seen);
                    break;

                case ICompoundAssignmentOperation compound when compound.OperatorMethod != null:
                    AddCall(ownerId, compound.OperatorMethod, operation.Syntax, edges, seen);
                    break;

                case IIncrementOrDecrementOperation increment when increment.OperatorMethod != null:
                    AddCall(ownerId, increment.OperatorMethod, operation.Syntax, edges, seen);
                    break;

                case IConversionOperation conversion:
                    if (conversion.OperatorMethod != null)
                        AddCall(ownerId, conversion.OperatorMethod, operation.Syntax, edges, seen);
                    if (!conversion.IsImplicit)
                        AddTypeUses(ownerId, conversion.Type, operation.Syntax, edges, seen);
                    break;

                // B19: implicit calls the syntax switch never sees. Each reads a
                // member Roslyn bound for the shape.
                case IRecursivePatternOperation recursivePattern:
                    AddTypeUses(ownerId, recursivePattern.MatchedType, operation.Syntax, edges, seen);
                    if (recursivePattern.DeconstructSymbol is IMethodSymbol deconstruct)
                        AddCall(ownerId, deconstruct, operation.Syntax, edges, seen);
                    break;

                case IListPatternOperation listPattern:
                    AddMemberBySymbolKind(ownerId, listPattern.LengthSymbol, operation.Syntax, edges, seen);
                    AddMemberBySymbolKind(ownerId, listPattern.IndexerSymbol, operation.Syntax, edges, seen);
                    break;

                case ISlicePatternOperation { SliceSymbol: { } sliceSymbol }:
                    AddMemberBySymbolKind(ownerId, sliceSymbol, operation.Syntax, edges, seen);
                    break;

                case IImplicitIndexerReferenceOperation implicitIndexer:
                    AddMemberBySymbolKind(ownerId, implicitIndexer.LengthSymbol, operation.Syntax, edges, seen);
                    if (implicitIndexer.IndexerSymbol is IPropertySymbol indexerProperty)
                        AddMemberAccess(owner, ownerId, indexerProperty, implicitIndexer, edges, seen);
                    else
                        AddCall(ownerId, implicitIndexer.IndexerSymbol as IMethodSymbol, operation.Syntax, edges, seen);
                    break;

                case ICollectionExpressionOperation { ConstructMethod: { } constructMethod } collection:
                    if (constructMethod.MethodKind == MethodKind.Constructor)
                    {
                        AddConstructs(ownerId, constructMethod.ContainingType, operation.Syntax, edges, seen);
                        if (!constructMethod.IsImplicitlyDeclared)
                            AddCall(ownerId, constructMethod, operation.Syntax, edges, seen);
                        AddCollectionAdds(ownerId, collection, model, edges, seen);
                    }
                    else
                    {
                        AddCall(ownerId, constructMethod, operation.Syntax, edges, seen);
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

                // B58: a local function is not a snapshot symbol, so the enclosing member
                // names the types in its signature.
                case ILocalFunctionOperation localFunction:
                    foreach (var parameter in localFunction.Symbol.Parameters)
                        AddTypeUses(ownerId, parameter.Type, operation.Syntax, edges, seen);
                    AddTypeUses(ownerId, localFunction.Symbol.ReturnType, operation.Syntax, edges, seen);
                    break;

                // D12, D136: a lambda or anonymous method depends on the type of each parameter and on
                // its return type, written or inferred, so a syntax-only rewrite changes no fact.
                case IAnonymousFunctionOperation anonymousFunction:
                    foreach (var parameter in anonymousFunction.Symbol.Parameters)
                        AddTypeUses(ownerId, parameter.Type, operation.Syntax, edges, seen);
                    AddTypeUses(ownerId, anonymousFunction.Symbol.ReturnType, operation.Syntax, edges, seen);
                    break;
            }
        }
    }

    private void AddTypeUses(
        string ownerId,
        ITypeSymbol? type,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (type == null)
            return;

        foreach (var used in ExtractionUtils.TypeUses(type))
            AddEdge(ownerId, used, nameof(EdgeKind.References), syntax, edges, seen);
    }

    private void ExtractAttributeConstructions(
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        foreach (var (owner, attribute) in ExecutableRoots.Attributes(context))
        {
            var ownerId = context.MakeSymbolId(owner);
            if (ownerId == null)
                continue;

            var syntax = BindingIncompletenessCollector.DeclaringSyntaxOrContainingType(owner);
            ExtractAttribute(owner, attribute, ownerId, syntax, edges, seen);
        }
    }

    private void ExtractAttribute(
        ISymbol owner,
        AttributeData attribute,
        string sourceId,
        SyntaxNode? syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        var attributeClass = attribute.AttributeClass;
        if (attributeClass == null)
            return;

        var targetId = context.MakeSymbolId(attributeClass);
        if (targetId == null)
            return;

        Emit(edges, seen, sourceId, targetId, nameof(EdgeKind.Constructs), syntax);

        // B28: an attribute application calls its constructor when the
        // constructor is declared in source.
        if (attribute.AttributeConstructor is { IsImplicitlyDeclared: false } attributeConstructor)
            AddEdge(sourceId, attributeConstructor, nameof(EdgeKind.Calls), syntax, edges, seen);

        // B33: an attribute argument is a root; walk its expression.
        if (attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax { ArgumentList: { } argumentList } &&
            context.IsInScope(argumentList.SyntaxTree))
        {
            var model = context.GetOrCreateSemanticModel(argumentList.SyntaxTree);
            foreach (var argument in argumentList.Arguments)
            {
                if (model.GetOperation(argument.Expression) is { } argumentOperation)
                    Walk(argumentOperation, owner, sourceId, model, edges, seen);
            }
        }
    }

    /// <summary>
    ///     Records one edge from <paramref name="ownerId" /> to <paramref name="target" />. A target
    ///     with no snapshot id is recorded as filtered external. An edge from a member to itself is
    ///     a fact (D13, D134); the readers ignore it.
    /// </summary>
    private void AddEdge(
        string ownerId,
        ISymbol target,
        string kind,
        SyntaxNode? syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen,
        string? receiverConstraintsJson = null)
    {
        context.RecordFilteredExternal(target, syntax);
        var targetId = context.MakeSymbolId(target);
        if (targetId == null)
            return;

        Emit(edges, seen, ownerId, targetId, kind, syntax, receiverConstraintsJson);
    }

    /// <summary>
    ///     An explicit invocation: the call (with the receiver-type constraints of the call
    ///     site), the <c>ExtensionReceiver</c> of a reduced extension call, the type named as a
    ///     static receiver, and the method's type arguments.
    /// </summary>
    private void AddInvocation(
        ISymbol owner,
        string ownerId,
        IInvocationOperation invocation,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        var syntax = invocation.Syntax;
        var method = invocation.TargetMethod;
        AddCall(ownerId, method, syntax, edges, seen, GetReceiverTypeConstraints(invocation, owner, model),
            IsBaseAccess(invocation.Instance));
        AddExtensionReceiver(invocation, edges, seen);
        AddStaticReceiverReference(ownerId, invocation, method, model, edges, seen);
        foreach (var typeArgument in method.TypeArguments)
            AddTypeUses(ownerId, typeArgument, syntax, edges, seen);
    }

    /// <summary>
    ///     The static receiver type of a call site, as a constraint the dispatch tiers can
    ///     replay: the receiver expression's type, the containing type for an implicit
    ///     <c>this</c>, and none for a static call, a <c>base</c> call or a type-name receiver.
    /// </summary>
    private string? GetReceiverTypeConstraints(IInvocationOperation invocation, ISymbol owner, SemanticModel model)
    {
        if (invocation.Syntax is not InvocationExpressionSyntax syntax)
            return null;
        if (model.GetSymbolInfo(syntax).Symbol is not IMethodSymbol callee ||
            callee is { IsStatic: true, ReducedFrom: null })
            return null;

        var receiverType = syntax.Expression switch
        {
            MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } => null,
            MemberAccessExpressionSyntax memberAccess
                when model.GetSymbolInfo(memberAccess.Expression).Symbol is INamedTypeSymbol => null,
            MemberAccessExpressionSyntax memberAccess => model.GetTypeInfo(memberAccess.Expression).Type,
            MemberBindingExpressionSyntax => syntax.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } conditional
                ? model.GetTypeInfo(conditional.Expression).Type
                : null,
            IdentifierNameSyntax or GenericNameSyntax when !owner.IsStatic => owner.ContainingType,
            _ => null
        };

        return ReceiverTypeConstraints.FromReceiverType(receiverType, context);
    }

    /// <summary>
    ///     A reduced extension call (<c>x.M()</c>, or an extension <c>Add</c> of a collection
    ///     initializer) binds its receiver type to the extension method. The receiver is the
    ///     first argument, which the source does not write as an argument.
    /// </summary>
    private void AddExtensionReceiver(
        IInvocationOperation invocation,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (!invocation.TargetMethod.IsExtensionMethod ||
            invocation.Arguments.Length == 0 ||
            invocation.Arguments[0].Syntax is ArgumentSyntax)
            return;

        var receiver = invocation.Arguments[0].Value;
        while (receiver is IConversionOperation { IsImplicit: true } conversion)
            receiver = conversion.Operand;
        if (receiver.Type is not INamedTypeSymbol receiverType)
            return;

        var receiverTypeId = context.MakeSymbolId(receiverType);
        var methodId = context.MakeSymbolId(invocation.TargetMethod);
        if (receiverTypeId == null || methodId == null)
            return;

        Emit(edges, seen, receiverTypeId, methodId, nameof(EdgeKind.ExtensionReceiver), invocation.Syntax);
    }

    private void AddCall(
        string ownerId,
        IMethodSymbol? method,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen,
        string? receiverConstraintsJson = null,
        bool throughBase = false)
    {
        if (method == null)
            return;

        // Local functions cannot be invoked outside their lexical method and are
        // never declared as snapshot symbols, so a Calls edge to one would orphan
        // out; anonymous-function symbols are compiler constructs, not members.
        if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            return;

        if (method is { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true })
            return;

        AddEdge(ownerId, method, nameof(EdgeKind.Calls), syntax, edges, seen, receiverConstraintsJson);
        if (!throughBase)
            AddDispatchMarker(ownerId, method, syntax, edges, seen);
    }

    /// <summary>
    ///     D22: an access whose target can dispatch at run time (<see cref="ExtractionUtils.IsDispatchPoint" />)
    ///     is also a <c>StaticallyCalls</c> fact, so a reader finds every dispatch point from one edge kind.
    ///     The caller leaves out a <c>base.</c> access, which never dispatches.
    /// </summary>
    private void AddDispatchMarker(
        string ownerId,
        ISymbol target,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (ExtractionUtils.IsDispatchPoint(target) && context.MakeSymbolId(target) is { } targetId)
            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.StaticallyCalls), syntax);
    }

    /// <summary>True when <paramref name="instance" /> is <c>base</c>: the access calls the base member without dispatch.</summary>
    private static bool IsBaseAccess(IOperation? instance)
    {
        return instance?.Syntax is BaseExpressionSyntax;
    }

    /// <summary>
    ///     D22: the accesses that run an accessor of the member, so they dispatch like a call: a
    ///     property or indexer access (also an implicit index or range access) and an event
    ///     <c>+=</c> or <c>-=</c>. Another use of an event (a raise of a field-like event) reads its
    ///     field and runs no accessor. A <c>base.</c> access does not dispatch.
    /// </summary>
    private static bool RunsAccessor(IOperation operation)
    {
        return operation switch
        {
            IPropertyReferenceOperation property => !IsBaseAccess(property.Instance),
            IImplicitIndexerReferenceOperation implicitIndexer => !IsBaseAccess(implicitIndexer.Instance),
            IEventReferenceOperation eventReference =>
                eventReference.Parent is IEventAssignmentOperation && !IsBaseAccess(eventReference.Instance),
            _ => false
        };
    }

    /// <summary>
    ///     The thrown type of a <c>throw</c>: the static type of the thrown expression before the
    ///     implicit conversion to <see cref="Exception" />. The filtered-external site is the
    ///     thrown expression, the edge location is the <c>throw</c>.
    /// </summary>
    private void AddThrows(
        string ownerId,
        IOperation thrown,
        SyntaxNode throwSyntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        while (thrown is IConversionOperation { IsImplicit: true } conversion)
            thrown = conversion.Operand;
        if (thrown.Type is not INamedTypeSymbol { TypeKind: not TypeKind.Error } exceptionType)
            return;

        context.RecordFilteredExternal(exceptionType, thrown.Syntax);
        var targetId = context.MakeSymbolId(exceptionType);
        if (targetId != null)
            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Throws), throwSyntax);
    }

    /// <summary>
    ///     C# 12.15.3: a user-defined <c>x &amp;&amp; y</c> runs <c>T.false(x)</c>, a user-defined
    ///     <c>x || y</c> runs <c>T.true(x)</c>, where T declares the selected <c>&amp;</c> or <c>|</c>
    ///     operator and both <c>true</c> and <c>false</c>. Roslyn keeps this operator internal.
    /// </summary>
    private static IMethodSymbol? ShortCircuitOperator(IBinaryOperation binary)
    {
        var name = binary.OperatorKind switch
        {
            BinaryOperatorKind.ConditionalAnd => WellKnownMemberNames.FalseOperatorName,
            BinaryOperatorKind.ConditionalOr => WellKnownMemberNames.TrueOperatorName,
            _ => null
        };
        if (name == null || binary.OperatorMethod is not { } selected)
            return null;

        return selected.ContainingType.GetMembers(name).OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 1 &&
                                      SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, selected.ReturnType));
    }

    private void AddMethodGroupRef(
        string ownerId,
        IMethodSymbol? method,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (method == null)
            return;
        if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
            return;

        AddEdge(ownerId, method, nameof(EdgeKind.MethodGroupRef), syntax, edges, seen);
    }

    private void AddConstructs(
        string ownerId,
        ITypeSymbol type,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        AddEdge(ownerId, type, nameof(EdgeKind.Constructs), syntax, edges, seen);
    }

    private void AddMemberRead(
        string ownerId,
        IPropertySymbol? property,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (property == null)
            return;

        AddEdge(ownerId, property, nameof(EdgeKind.Reads), syntax, edges, seen);
        AddDispatchMarker(ownerId, property, syntax, edges, seen);
    }

    private void AddMemberAccess(
        ISymbol owner,
        string ownerId,
        ISymbol member,
        IOperation operation,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        // A nameof argument is a compile-time name, not a read
        // (NameOfReflectionExtractor records it as ReflectionMemberRef).
        if (NameOfReflectionExtractor.IsInsideNameOf(operation.Syntax))
            return;

        context.RecordFilteredExternal(member, operation.Syntax);
        var targetId = context.MakeSymbolId(member);
        if (targetId == null)
            return;

        var (reads, writes) = ClassifyAccess(operation, owner);
        if (reads)
            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Reads), operation.Syntax);
        if (writes)
            Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.Writes), operation.Syntax);
        if (RunsAccessor(operation))
            AddDispatchMarker(ownerId, member, operation.Syntax, edges, seen);
    }

    /// <summary>
    ///     D11: '=', an out argument and a tuple target of a deconstruction write. A compound
    ///     assignment, '??=', '++', '--', a ref argument and a writable ref alias (a ref local,
    ///     a ref return, a ref reassignment) read and write. A ref readonly alias, an in
    ///     argument and every other use read. An event: '+=' and '-=' write.
    /// </summary>
    private static (bool Reads, bool Writes) ClassifyAccess(IOperation operation, ISymbol owner)
    {
        switch (operation.Parent)
        {
            case IEventAssignmentOperation eventAssignment when ReferenceEquals(eventAssignment.EventReference, operation):
                return (false, true);
            case ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, operation):
            case ICoalesceAssignmentOperation coalesce when ReferenceEquals(coalesce.Target, operation):
            case IIncrementOrDecrementOperation increment when ReferenceEquals(increment.Target, operation):
                return (true, true);
            case ISimpleAssignmentOperation assignment when ReferenceEquals(assignment.Target, operation):
                return (false, true);
            case ISimpleAssignmentOperation { IsRef: true } refAssignment:
                return Alias(RefKindOf(refAssignment.Target));
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                return Alias(declarator.Symbol.RefKind);
            case IReturnOperation returnOperation:
                return Alias(ReturnRefKind(returnOperation, owner));
            case IConditionalOperation { IsRef: true } conditional when !ReferenceEquals(conditional.Condition, operation):
                return ClassifyAccess(conditional, owner);
            case ITupleOperation tuple:
                return IsDeconstructionTarget(tuple) ? (false, true) : (true, false);
            case IArgumentOperation argument:
                return argument.Parameter?.RefKind switch
                {
                    RefKind.Out => (false, true),
                    RefKind.Ref => (true, true),
                    _ => (true, false)
                };
            default:
                return (true, false);
        }

        static (bool Reads, bool Writes) Alias(RefKind refKind) => (true, refKind == RefKind.Ref);
    }

    private static RefKind RefKindOf(IOperation target)
    {
        return target switch
        {
            ILocalReferenceOperation local => local.Local.RefKind,
            IParameterReferenceOperation parameter => parameter.Parameter.RefKind,
            IFieldReferenceOperation field => field.Field.RefKind,
            _ => RefKind.None
        };
    }

    /// <summary>The ref kind that the function around <paramref name="returnOperation" /> returns.</summary>
    private static RefKind ReturnRefKind(IReturnOperation returnOperation, ISymbol owner)
    {
        for (var current = returnOperation.Parent; current != null; current = current.Parent)
        {
            switch (current)
            {
                case IAnonymousFunctionOperation anonymousFunction:
                    return anonymousFunction.Symbol.RefKind;
                case ILocalFunctionOperation localFunction:
                    return localFunction.Symbol.RefKind;
            }
        }

        return owner is IMethodSymbol method ? method.RefKind : RefKind.None;
    }

    /// <summary>True when <paramref name="tuple" /> is (inside) the target of a deconstruction.</summary>
    private static bool IsDeconstructionTarget(ITupleOperation tuple)
    {
        IOperation outer = tuple;
        while (outer.Parent is ITupleOperation parentTuple)
            outer = parentTuple;
        return outer.Parent is IDeconstructionAssignmentOperation deconstruction &&
               ReferenceEquals(deconstruction.Target, outer);
    }

    /// <summary>
    ///     D124: a static member access names its receiver type (an invocation, a method group, a
    ///     field, a property, an event or a constant), with the type's arguments. An unqualified
    ///     access and a <c>nameof</c> argument name no receiver.
    /// </summary>
    private void AddStaticReceiverReference(
        string ownerId,
        IOperation operation,
        ISymbol member,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (!member.IsStatic)
            return;

        var accessSyntax = operation.Syntax is InvocationExpressionSyntax invocation ? invocation.Expression : operation.Syntax;
        if (accessSyntax is not MemberAccessExpressionSyntax memberAccess ||
            NameOfReflectionExtractor.IsInsideNameOf(memberAccess))
            return;
        if (model.GetSymbolInfo(memberAccess.Expression).Symbol is not INamedTypeSymbol receiverType)
            return;

        // The edge sits on the receiver name. The filtered-external site is the access itself,
        // so one access stays one site (D16).
        foreach (var used in ExtractionUtils.TypeUses(receiverType))
        {
            context.RecordFilteredExternal(used, operation.Syntax);
            if (context.MakeSymbolId(used) is { } targetId)
                Emit(edges, seen, ownerId, targetId, nameof(EdgeKind.References), memberAccess.Expression);
        }
    }

    private void AddPrimaryBaseConstructor(
        string ownerId,
        PrimaryConstructorBaseTypeSyntax primaryBase,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
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

        AddCall(ownerId, resolved, primaryBase, edges, seen);
    }

    private void AddUsingDispose(
        string ownerId,
        IOperation? resources,
        bool isAsync,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        var resourceTypes = new List<ITypeSymbol>();
        switch (resources)
        {
            // D10: the static type of the resource is the type of each declared local.
            case IVariableDeclarationGroupOperation group:
                foreach (var declaration in group.Declarations)
                foreach (var declarator in declaration.Declarators)
                    resourceTypes.Add(declarator.Symbol.Type);

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
                AddCall(ownerId, disposeMethod, syntax, edges, seen);
        }
    }

    /// <summary>
    ///     D10: the member that implements <c>IDisposable.Dispose</c> (<c>IAsyncDisposable.DisposeAsync</c>)
    ///     on the static type of the resource; a ref struct uses its own Dispose pattern method. A type
    ///     parameter resolves through its effective base class, else to the interface method. A nullable
    ///     value type disposes its underlying type. <c>dynamic</c> has no target.
    /// </summary>
    private IMethodSymbol? ResolveDisposeMethod(ITypeSymbol type, bool isAsync)
    {
        var methodName = isAsync ? "DisposeAsync" : "Dispose";
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        if (type is INamedTypeSymbol { IsRefLikeType: true } refStruct)
            return refStruct.GetMembers(methodName).OfType<IMethodSymbol>()
                .FirstOrDefault(method => method is { IsStatic: false, Parameters.Length: 0 });

        var disposableType = isAsync
            ? context.Compilation.GetTypeByMetadataName("System.IAsyncDisposable")
            : context.Compilation.GetSpecialType(SpecialType.System_IDisposable);
        if (disposableType is null || disposableType.TypeKind == TypeKind.Error)
            return null;
        var interfaceMethod = disposableType.GetMembers(methodName).OfType<IMethodSymbol>()
            .FirstOrDefault(method => method.Parameters.Length == 0);
        if (interfaceMethod == null)
            return null;

        var implementingType = type switch
        {
            ITypeParameterSymbol typeParameter => EffectiveBaseClass(typeParameter),
            INamedTypeSymbol named => named,
            _ => null
        };
        if (implementingType == null)
            return type is ITypeParameterSymbol ? interfaceMethod : null;

        return implementingType.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol ?? interfaceMethod;
    }

    /// <summary>The class constraint of <paramref name="typeParameter" />, also through another type parameter.</summary>
    private static INamedTypeSymbol? EffectiveBaseClass(ITypeParameterSymbol typeParameter)
    {
        var visited = new HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
        var pending = new Stack<ITypeParameterSymbol>([typeParameter]);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            foreach (var constraint in current.ConstraintTypes)
            {
                switch (constraint)
                {
                    case INamedTypeSymbol { TypeKind: TypeKind.Class } classType:
                        return classType;
                    case ITypeParameterSymbol other:
                        pending.Push(other);
                        break;
                }
            }
        }

        return null;
    }

    private void AddAwaitPattern(
        string ownerId,
        AwaitExpressionSyntax syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        var info = model.GetAwaitExpressionInfo(syntax);
        AddCall(ownerId, info.GetAwaiterMethod, syntax, edges, seen);
        AddCall(ownerId, info.GetResultMethod, syntax, edges, seen);
        AddMemberRead(ownerId, info.IsCompletedProperty, syntax, edges, seen);
    }

    private void AddForeachPattern(
        string ownerId,
        CommonForEachStatementSyntax syntax,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        AddEnumerationPattern(ownerId, model.GetForEachStatementInfo(syntax), syntax, edges, seen);

        // A foreach deconstruction ('foreach (var (a, b) in pairs)') has its own
        // Deconstruct calls, reached through the same Nested recursion as an
        // assignment deconstruction.
        if (syntax is ForEachVariableStatementSyntax variableSyntax)
            AddDeconstructionCalls(ownerId, model.GetDeconstructionInfo(variableSyntax), variableSyntax,
                edges, seen);
    }

    /// <summary>The implicit members of a foreach enumeration (also of a spread element).</summary>
    private void AddEnumerationPattern(
        string ownerId,
        ForEachStatementInfo info,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        AddCall(ownerId, info.GetEnumeratorMethod, syntax, edges, seen);
        AddCall(ownerId, info.MoveNextMethod, syntax, edges, seen);
        AddMemberRead(ownerId, info.CurrentProperty, syntax, edges, seen);
        AddCall(ownerId, info.DisposeMethod, syntax, edges, seen);
    }

    /// <summary>
    ///     C#: a collection expression of a type with an applicable <c>Add</c> calls <c>Add</c> for
    ///     each expression element and for each item of a spread element.
    /// </summary>
    private void AddCollectionAdds(
        string ownerId,
        ICollectionExpressionOperation collection,
        SemanticModel model,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        if (collection.Type is not { } collectionType)
            return;

        foreach (var element in collection.Elements)
        {
            var argument = element is ISpreadOperation spread
                ? SpeculativePatterns.ItemOf(spread.ElementType)
                : element.Syntax as ExpressionSyntax;
            if (argument != null)
                AddCall(ownerId, SpeculativePatterns.AddMethod(model, collection.Syntax.SpanStart, collectionType, argument),
                    collection.Syntax, edges, seen);
        }
    }

    private void AddDeconstructionCalls(
        string ownerId,
        DeconstructionInfo info,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        AddCall(ownerId, info.Method, syntax, edges, seen);
        foreach (var nested in info.Nested)
            AddDeconstructionCalls(ownerId, nested, syntax, edges, seen);
    }

    private void AddMemberBySymbolKind(
        string ownerId,
        ISymbol? member,
        SyntaxNode syntax,
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen)
    {
        switch (member)
        {
            case IPropertySymbol property:
                AddMemberRead(ownerId, property, syntax, edges, seen);
                break;
            case IMethodSymbol method:
                AddCall(ownerId, method, syntax, edges, seen);
                break;
        }
    }

    /// <summary>
    ///     Adds one edge for each (source, target, kind); the first site gives the location.
    ///     A later site of the same <c>Calls</c> relation adds its receiver-type constraints.
    /// </summary>
    private void Emit(
        List<EdgeRecord> edges,
        Dictionary<(string Source, string Target, string Kind), int> seen,
        string sourceId,
        string targetId,
        string kind,
        SyntaxNode? syntax,
        string? receiverConstraintsJson = null)
    {
        var key = (sourceId, targetId, kind);
        if (seen.TryGetValue(key, out var position))
        {
            edges[position] = ReceiverTypeConstraints.WithMergedConstraints(edges[position], receiverConstraintsJson);
            return;
        }

        var location = syntax != null
            ? context.GetLocationInfo(syntax.GetLocation())
            : ((string? path, int? sl, int? sc, int? el, int? ec)?)null;
        var edge = context.MakeEdge(sourceId, targetId, kind, ExtractorConstants.OperationShapesExtractor, location);
        seen.Add(key, edges.Count);
        edges.Add(ReceiverTypeConstraints.WithMergedConstraints(edge, receiverConstraintsJson));
    }
}
