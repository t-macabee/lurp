using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Lurp.Tests;

/// <summary>
///     Audit B1 Oracle A: an independent walk over the fixture compilations'
///     Roslyn operations. It derives the fact set directly from every syntax
///     tree: an operation root is any node whose
///     <see cref="SemanticModel.GetOperation(SyntaxNode)" /> is non-null while
///     its parent's is null, plus attribute arguments and constructor
///     initializers, so no root list is shared with the extractor. Await,
///     foreach, deconstruction and the C# pattern, index/range and
///     collection-expression shapes are read from Roslyn's binding answers, not a
///     copy of extractor logic; the <c>using</c>/<c>Dispose</c> resolver is the one
///     mapping shared with the extractor, because Roslyn exposes no public
///     <c>using</c> info. The reviewed
///     golden (Oracle C) is the human check on this mapping; the exact-set test
///     fences the extractor against drift for the whole corpus.
/// </summary>
/// <remarks>
///     Owner mapping: the enclosing symbol from
///     <see cref="SemanticModel.GetEnclosingSymbol(int, System.Threading.CancellationToken)" />
///     is normalized to the id the extractor persists — lambdas and local
///     functions map to their containing member, a field initializer to the
///     field, a property initializer to the property, an expression-bodied
///     property or indexer to its getter, and a primary-constructor base list
///     to the primary constructor. This file does not reference
///     <c>Lurp.Workspace</c>.
/// </remarks>
internal static class OperationShapeOracle
{
    /// <summary>
    ///     The extraction-side canonical doc-comment id for a symbol: un-reduce
    ///     classic extensions and map C# 14 extension-block members to the
    ///     declared member on the outer static class. Used by Oracle B to compare
    ///     Roslyn's caller/target symbols with persisted edge endpoints.
    /// </summary>
    public static string? NormalizedDocId(ISymbol symbol)
    {
        return OracleContext.DocId(symbol);
    }

    public static HashSet<(string Source, string Kind, string Target)> Extract(
        IReadOnlyList<(string Project, Compilation Compilation)> projects)
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, compilation) in projects)
        {
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                AddDeclared(type, declared);
                foreach (var member in type.GetMembers())
                {
                    if (member is INamedTypeSymbol)
                        continue;
                    // B3 step 2a: a C# 14 extension block declares a block member
                    // and its implementation on the outer static class. Only the
                    // implementation is canonical; the block method is not declared.
                    if (member is IMethodSymbol { ContainingType.IsExtension: true })
                        continue;
                    AddDeclared(member, declared);
                }
            }

            if (compilation.GetEntryPoint(CancellationToken.None) is { } entryPoint)
                AddDeclared(entryPoint, declared);
        }

        var facts = new HashSet<(string Source, string Kind, string Target)>();
        foreach (var (_, compilation) in projects)
        {
            var context = new OracleContext(compilation, declared, facts);
            context.Run();
        }

        return facts;
    }

    private static void AddDeclared(ISymbol symbol, HashSet<string> declared)
    {
        if (symbol.GetDocumentationCommentId() is { Length: > 0 } id)
            declared.Add(id);
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Descendants(type))
                yield return nested;
        }

        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in AllTypes(child))
                yield return type;

        static IEnumerable<INamedTypeSymbol> Descendants(INamedTypeSymbol type)
        {
            foreach (var nested in type.GetTypeMembers())
            {
                yield return nested;
                foreach (var deeper in Descendants(nested))
                    yield return deeper;
            }
        }
    }

    private sealed class OracleContext(
        Compilation compilation,
        HashSet<string> declared,
        HashSet<(string Source, string Kind, string Target)> facts)
    {
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

        public void Run()
        {
            // Independent root discovery: walk every syntax tree top-down. A node
            // is an operation root when it has an operation and its parent does
            // not, so every executable root is found without sharing a root list
            // with the extractor. Constructor initializers have no operation of
            // their own and are bound directly; attribute arguments are roots even
            // under an operation-bearing parent.
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = Model(tree);
                foreach (var node in tree.GetRoot().DescendantNodesAndSelf())
                {
                    if (node is ConstructorInitializerSyntax initializer)
                    {
                        if (FindOwner(node, model) is not { } initializerOwner ||
                            DocId(initializerOwner) is not { } initializerOwnerId)
                            continue;
                        if (model.GetSymbolInfo(initializer).Symbol is IMethodSymbol initializerTarget)
                            AddCall(initializerOwnerId, initializerTarget, initializer, model);
                        continue;
                    }

                    if (node is PrimaryConstructorBaseTypeSyntax primaryBase)
                    {
                        if (FindOwner(node, model) is not { } primaryOwner ||
                            DocId(primaryOwner) is not { } primaryOwnerId)
                            continue;
                        AddPrimaryBase(primaryOwnerId, primaryBase, model);
                        continue;
                    }

                    var operation = model.GetOperation(node);
                    if (operation == null)
                        continue;
                    // Attribute applications are emitted symbol-side by
                    // AddAttributeConstructions (exactly as the extractor does);
                    // their argument expressions are still walked as roots below.
                    if (node is CompilationUnitSyntax or AttributeSyntax)
                        continue;
                    if (node is not AttributeArgumentSyntax &&
                        node.Parent != null &&
                        model.GetOperation(node.Parent) != null)
                        continue;

                    if (FindOwner(node, model) is not { } owner || DocId(owner) is not { } ownerId)
                        continue;

                    Walk(operation, ownerId, model);
                }
            }

            AddAttributeConstructions();
        }

        private static int ProbePosition(SyntaxNode node)
        {
            return node switch
            {
                MethodDeclarationSyntax { Body: { } body } => body.SpanStart,
                MethodDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                ConstructorDeclarationSyntax { Body: { } body } => body.SpanStart,
                ConstructorDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                OperatorDeclarationSyntax { Body: { } body } => body.SpanStart,
                OperatorDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                ConversionOperatorDeclarationSyntax { Body: { } body } => body.SpanStart,
                ConversionOperatorDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                DestructorDeclarationSyntax { Body: { } body } => body.SpanStart,
                DestructorDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                AccessorDeclarationSyntax { Body: { } body } => body.SpanStart,
                AccessorDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                PropertyDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                PropertyDeclarationSyntax { Initializer.Value: { } value } => value.SpanStart,
                IndexerDeclarationSyntax { ExpressionBody.Expression: { } expression } => expression.SpanStart,
                _ => node.SpanStart
            };
        }

        /// <summary>
        ///     The extraction owner for a root: the symbol whose body the root
        ///     belongs to, mapped to the ids the extractor persists. Lambdas and
        ///     local functions map to the containing member (they are never
        ///     snapshot symbols), a field initializer to the field, a property
        ///     initializer to the property, an expression-bodied property/indexer
        ///     to its getter, and a primary-constructor base list to the primary
        ///     constructor. Everything else is the compiler's enclosing symbol.
        /// </summary>
        private static ISymbol? FindOwner(SyntaxNode node, SemanticModel model)
        {
            // Primary-constructor base arguments: GetEnclosingSymbol resolves the
            // enclosing namespace there, so take the primary constructor from the
            // containing type declaration directly (same pick as the extractor).
            if (node.FirstAncestorOrSelf<PrimaryConstructorBaseTypeSyntax>() is { } primaryBase &&
                primaryBase.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } typeDeclaration &&
                model.GetDeclaredSymbol(typeDeclaration) is INamedTypeSymbol declaredType)
                return declaredType.InstanceConstructors.FirstOrDefault(constructor => !constructor.IsImplicitlyDeclared) ?? (ISymbol)declaredType;

            // An attribute argument belongs to the attributed symbol, not the
            // enclosing binder: the type or member for a type/member attribute, the
            // method (or indexer property) for a parameter or return-value attribute.
            if (node.FirstAncestorOrSelf<AttributeArgumentSyntax>() is not null)
            {
                var attributeList = node.FirstAncestorOrSelf<AttributeListSyntax>();
                var parent = attributeList?.Parent;
                if (attributeList is null || parent is null ||
                    attributeList.Target?.Identifier.ValueText is "assembly" or "module" ||
                    parent is CompilationUnitSyntax)
                    return null;

                if (parent is ParameterSyntax parameter)
                    return model.GetDeclaredSymbol(parameter)?.ContainingSymbol;

                if (attributeList.Target?.Identifier.ValueText == "return")
                    return model.GetDeclaredSymbol(parent);

                if (parent is BaseFieldDeclarationSyntax fieldDeclaration)
                {
                    var variable = fieldDeclaration.Declaration.Variables.FirstOrDefault();
                    return variable is null ? null : model.GetDeclaredSymbol(variable);
                }

                return model.GetDeclaredSymbol(parent);
            }

            // Probe inside the executable body: GetEnclosingSymbol resolves through
            // the enclosing binder, which for a declaration only exists inside the
            // body, not on the signature.
            var position = ProbePosition(node);
            var owner = model.GetEnclosingSymbol(position, CancellationToken.None);
            if (owner == null)
                return null;

            while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } nested)
                owner = nested.ContainingSymbol;

            // R4.5: a delegate parameter default's enclosing symbol is the delegate's Invoke
            // method, which is not a stored symbol; extraction attributes it to the delegate type.
            if (owner is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke } delegateInvoke)
                owner = delegateInvoke.ContainingType;

            // An auto-property initializer's enclosing symbol is the compiler
            // backing field; extraction attributes it to the property.
            if (owner is IFieldSymbol { AssociatedSymbol: IPropertySymbol associatedProperty })
                owner = associatedProperty;

            // A field-like event initializer's enclosing symbol is the compiler
            // backing field; extraction attributes it to the event (B33).
            if (owner is IFieldSymbol { AssociatedSymbol: IEventSymbol associatedEvent })
                owner = associatedEvent;

            if (owner is IMethodSymbol { AssociatedSymbol: IPropertySymbol associated })
            {
                if (node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { ExpressionBody: not null } ||
                    node.FirstAncestorOrSelf<IndexerDeclarationSyntax>() is { ExpressionBody: not null })
                    return associated.GetMethod ?? owner;
                if (node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { Initializer: not null })
                    return associated;
            }

            return owner;
        }

        private SemanticModel Model(SyntaxTree tree)
        {
            if (!_models.TryGetValue(tree, out var model))
            {
                model = compilation.GetSemanticModel(tree);
                _models[tree] = model;
            }

            return model;
        }

        private void Walk(IOperation root, string ownerId, SemanticModel model)
        {
            foreach (var operation in root.DescendantsAndSelf())
            {
                switch (operation)
                {
                    case IInvocationOperation invocation:
                        if (operation.Parent is IInterpolatedStringHandlerCreationOperation)
                            break;
                        AddCall(ownerId, invocation.TargetMethod, operation.Syntax, model);
                        AddStaticReceiverReference(ownerId, operation.Syntax, model);
                        foreach (var typeArgument in invocation.TargetMethod.TypeArguments)
                            AddTypeUses(ownerId, typeArgument, operation.Syntax);
                        break;
                    case IObjectCreationOperation creation:
                        if (operation.Parent is IInterpolatedStringHandlerCreationOperation)
                            break;
                        if (creation.Type is { } createdType)
                        {
                            if (DocId(createdType) is { } createdId && createdId != ownerId)
                                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Constructs), createdId, operation.Syntax);
                            foreach (var nestedType in NestedTypeUses(createdType))
                                AddTypeUses(ownerId, nestedType, operation.Syntax);
                        }

                        if (creation.Constructor is { IsImplicitlyDeclared: false } constructor)
                            AddCall(ownerId, constructor, operation.Syntax, model);
                        break;
                    case IMethodReferenceOperation methodReference:
                        AddCallLike(ownerId, methodReference.Method, nameof(Lurp.Storage.EdgeKind.MethodGroupRef), operation.Syntax);
                        break;
                    case IInterpolatedStringAppendOperation append when append.AppendCall is IInvocationOperation appendCall:
                        AddCall(ownerId, appendCall.TargetMethod, operation.Syntax, model);
                        break;
                    case IAwaitOperation awaitOperation when awaitOperation.Syntax is AwaitExpressionSyntax awaitSyntax:
                        AddAwait(ownerId, awaitSyntax, model);
                        break;
                    case IForEachLoopOperation when operation.Syntax is CommonForEachStatementSyntax loopSyntax:
                        AddForeach(ownerId, loopSyntax, model);
                        break;
                    case IUsingDeclarationOperation usingDeclaration:
                        AddUsingDispose(ownerId, usingDeclaration.DeclarationGroup, usingDeclaration.IsAsynchronous, operation.Syntax, model);
                        break;
                    case IUsingOperation usingOperation:
                        AddUsingDispose(ownerId, usingOperation.Resources, usingOperation.IsAsynchronous, operation.Syntax, model);
                        break;
                    case IDeconstructionAssignmentOperation deconstruction when deconstruction.Syntax is AssignmentExpressionSyntax assignment:
                        AddDeconstruction(ownerId, model.GetDeconstructionInfo(assignment), assignment);
                        break;
                    case IPropertyReferenceOperation propertyReference:
                        AddMemberAccess(ownerId, propertyReference.Property, propertyReference);
                        break;
                    case IFieldReferenceOperation fieldReference:
                        AddMemberAccess(ownerId, fieldReference.Field, fieldReference);
                        break;
                    case IEventReferenceOperation eventReference:
                        AddMemberAccess(ownerId, eventReference.Event, eventReference);
                        break;
                    case IUnaryOperation unary when unary.OperatorMethod != null:
                        AddCall(ownerId, unary.OperatorMethod, operation.Syntax, model);
                        break;
                    case IBinaryOperation binary when binary.OperatorMethod != null:
                        AddCall(ownerId, binary.OperatorMethod, operation.Syntax, model);
                        break;
                    case ICompoundAssignmentOperation compound when compound.OperatorMethod != null:
                        AddCall(ownerId, compound.OperatorMethod, operation.Syntax, model);
                        break;
                    case IIncrementOrDecrementOperation increment when increment.OperatorMethod != null:
                        AddCall(ownerId, increment.OperatorMethod, operation.Syntax, model);
                        break;
                    case IConversionOperation conversion:
                        if (conversion.OperatorMethod != null)
                            AddCall(ownerId, conversion.OperatorMethod, operation.Syntax, model);
                        if (!conversion.IsImplicit)
                            AddTypeUses(ownerId, conversion.Type, operation.Syntax);
                        break;

                    // B19: implicit calls the syntax switch never sees. Each reads a
                    // member Roslyn bound for the shape.
                    case IRecursivePatternOperation recursivePattern:
                        AddTypeUses(ownerId, recursivePattern.MatchedType, operation.Syntax);
                        if (recursivePattern.DeconstructSymbol is IMethodSymbol deconstruct)
                            AddCallLike(ownerId, deconstruct, nameof(Lurp.Storage.EdgeKind.Calls), operation.Syntax);
                        break;
                    case IListPatternOperation listPattern:
                        AddMemberBySymbolKind(ownerId, listPattern.LengthSymbol, operation.Syntax);
                        AddMemberBySymbolKind(ownerId, listPattern.IndexerSymbol, operation.Syntax);
                        break;
                    case ISlicePatternOperation { SliceSymbol: { } sliceSymbol }:
                        AddMemberBySymbolKind(ownerId, sliceSymbol, operation.Syntax);
                        break;
                    case IImplicitIndexerReferenceOperation implicitIndexer:
                        AddMemberBySymbolKind(ownerId, implicitIndexer.LengthSymbol, operation.Syntax);
                        if (implicitIndexer.IndexerSymbol is IPropertySymbol indexerProperty)
                            AddMemberAccess(ownerId, indexerProperty, implicitIndexer);
                        else
                            AddCall(ownerId, implicitIndexer.IndexerSymbol as IMethodSymbol, operation.Syntax, model);
                        break;
                    case ICollectionExpressionOperation { ConstructMethod: { } constructMethod }:
                        if (constructMethod.MethodKind == MethodKind.Constructor)
                        {
                            if (DocId(constructMethod.ContainingType) is { } constructedId && constructedId != ownerId)
                                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Constructs), constructedId, operation.Syntax);
                            if (!constructMethod.IsImplicitlyDeclared)
                                AddCall(ownerId, constructMethod, operation.Syntax, model);
                        }
                        else
                        {
                            AddCallLike(ownerId, constructMethod, nameof(Lurp.Storage.EdgeKind.Calls), operation.Syntax);
                        }

                        break;

                    // B30: every source type named in a body is a References target;
                    // typeof is left to ReflectionTypeRef.
                    case IIsTypeOperation isType:
                        AddTypeUses(ownerId, isType.TypeOperand, operation.Syntax);
                        break;
                    case IDeclarationPatternOperation declarationPattern:
                        AddTypeUses(ownerId, declarationPattern.MatchedType, operation.Syntax);
                        break;
                    case ITypePatternOperation typePattern:
                        AddTypeUses(ownerId, typePattern.MatchedType, operation.Syntax);
                        break;
                    case ISizeOfOperation sizeOf:
                        AddTypeUses(ownerId, sizeOf.TypeOperand, operation.Syntax);
                        break;
                    case IArrayCreationOperation arrayCreation:
                        AddTypeUses(ownerId, arrayCreation.Type, operation.Syntax);
                        break;
                    case IVariableDeclaratorOperation declarator:
                        AddTypeUses(ownerId, declarator.Symbol.Type, operation.Syntax);
                        break;
                    case ICatchClauseOperation catchClause:
                        AddTypeUses(ownerId, catchClause.ExceptionType, operation.Syntax);
                        break;
                    case IDefaultValueOperation defaultValue when defaultValue.Syntax is DefaultExpressionSyntax:
                        AddTypeUses(ownerId, defaultValue.Type, operation.Syntax);
                        break;
                }
            }
        }

        private void AddCall(string ownerId, IMethodSymbol? method, SyntaxNode syntax, SemanticModel model)
        {
            _ = syntax;
            _ = model;
            AddCallLike(ownerId, method, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
        }

        private void AddCallLike(string ownerId, IMethodSymbol? method, string kind, SyntaxNode syntax)
        {
            if (method == null)
                return;
            if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
                return;
            if (DocId(method) is not { } targetId || targetId == ownerId)
                return;
            Add(ownerId, kind, targetId, syntax);
        }

        private void AddMemberAccess(string ownerId, ISymbol member, IOperation operation)
        {
            if (DocId(member) is not { } targetId)
                return;

            var (reads, writes) = Classify(operation);
            if (reads)
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Reads), targetId, operation.Syntax);
            if (writes)
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Writes), targetId, operation.Syntax);
        }

        private static (bool Reads, bool Writes) Classify(IOperation operation)
        {
            return operation.Parent switch
            {
                IEventAssignmentOperation eventAssignment when ReferenceEquals(eventAssignment.EventReference, operation) => (false, true),
                IAssignmentOperation assignment when ReferenceEquals(assignment.Target, operation) => (false, true),
                ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, operation) => (true, true),
                IIncrementOrDecrementOperation increment when ReferenceEquals(increment.Target, operation) => (true, true),
                IArgumentOperation argument when argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out } => (false, true),
                _ => (true, false)
            };
        }

        private void AddMemberBySymbolKind(string ownerId, ISymbol? member, SyntaxNode syntax)
        {
            switch (member)
            {
                case IPropertySymbol property:
                    if (DocId(property) is { } propertyId)
                        Add(ownerId, nameof(Lurp.Storage.EdgeKind.Reads), propertyId, syntax);
                    break;
                case IMethodSymbol method:
                    AddCallLike(ownerId, method, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
                    break;
            }
        }

        private void AddTypeUses(string ownerId, ITypeSymbol? type, SyntaxNode syntax)
        {
            if (type == null)
                return;

            foreach (var used in TypeUses(type))
            {
                if (DocId(used) is not { } targetId || targetId == ownerId)
                    continue;
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.References), targetId, syntax);
            }
        }

        private static IEnumerable<INamedTypeSymbol> NestedTypeUses(ITypeSymbol type)
        {
            switch (type)
            {
                case IArrayTypeSymbol array:
                    foreach (var nested in TypeUses(array.ElementType))
                        yield return nested;
                    break;
                case INamedTypeSymbol named:
                    foreach (var argument in named.TypeArguments)
                        foreach (var nested in TypeUses(argument))
                            yield return nested;
                    break;
            }
        }

        private static IEnumerable<INamedTypeSymbol> TypeUses(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named && named.TypeKind != TypeKind.Error && !named.IsAnonymousType)
                yield return named.OriginalDefinition;

            foreach (var nested in NestedTypeUses(type))
                yield return nested;
        }

        private void AddStaticReceiverReference(string ownerId, SyntaxNode syntax, SemanticModel model)
        {
            if (syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess })
                return;
            if (model.GetSymbolInfo(memberAccess.Expression).Symbol is not INamedTypeSymbol containingType)
                return;
            if (DocId(containingType) is not { } targetId || targetId == ownerId)
                return;
            Add(ownerId, nameof(Lurp.Storage.EdgeKind.References), targetId, memberAccess.Expression);
        }

        private void AddPrimaryBase(string ownerId, PrimaryConstructorBaseTypeSyntax primaryBase, SemanticModel model)
        {
            // Emit no fact when the base constructor does not bind; guessing a
            // target from the argument types would record an unproved edge.
            if (model.GetSymbolInfo(primaryBase).Symbol is IMethodSymbol resolved)
                AddCallLike(ownerId, resolved, nameof(Lurp.Storage.EdgeKind.Calls), primaryBase);
        }

        private void AddAwait(string ownerId, AwaitExpressionSyntax syntax, SemanticModel model)
        {
            var info = model.GetAwaitExpressionInfo(syntax);
            AddCallLike(ownerId, info.GetAwaiterMethod, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            AddCallLike(ownerId, info.GetResultMethod, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            if (info.IsCompletedProperty is { } isCompleted && DocId(isCompleted) is { } isCompletedId)
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Reads), isCompletedId, syntax);
        }

        private void AddForeach(string ownerId, CommonForEachStatementSyntax syntax, SemanticModel model)
        {
            var info = model.GetForEachStatementInfo(syntax);
            AddCallLike(ownerId, info.GetEnumeratorMethod, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            AddCallLike(ownerId, info.MoveNextMethod, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            if (info.CurrentProperty is { } current && DocId(current) is { } currentId)
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Reads), currentId, syntax);
            AddCallLike(ownerId, info.DisposeMethod, nameof(Lurp.Storage.EdgeKind.Calls), syntax);

            // A foreach deconstruction ('foreach (var (a, b) in pairs)') has its own
            // Deconstruct calls, reached through the same Nested recursion as an
            // assignment deconstruction.
            if (syntax is ForEachVariableStatementSyntax variableSyntax)
                AddDeconstruction(ownerId, model.GetDeconstructionInfo(variableSyntax), variableSyntax);
        }

        private void AddUsingDispose(string ownerId, IOperation? resources, bool isAsync, SyntaxNode syntax, SemanticModel model)
        {
            var types = new List<ITypeSymbol>();
            if (resources is IVariableDeclarationGroupOperation group)
            {
                foreach (var declaration in group.Declarations)
                {
                    if (declaration.Type is { } type)
                        types.Add(type);
                    foreach (var declarator in declaration.Declarators)
                    {
                        if (declarator.Initializer?.Value.Type is { } initializerType)
                            types.Add(initializerType);
                    }
                }
            }
            else if (resources?.Type is { } resourceType)
            {
                types.Add(resourceType);
            }

            foreach (var type in types)
                AddCallLike(ownerId, ResolveDispose(type, isAsync), nameof(Lurp.Storage.EdgeKind.Calls), syntax);
        }

        // This is the only mapping shared with the extractor: Roslyn exposes no
        // public 'using' info, so both walks resolve Dispose from the resource type.
        // The golden file is its check.
        private IMethodSymbol? ResolveDispose(ITypeSymbol type, bool isAsync)
        {
            if (type is not INamedTypeSymbol namedType)
                return null;
            var name = isAsync ? "DisposeAsync" : "Dispose";
            var direct = namedType.GetMembers(name).OfType<IMethodSymbol>()
                .FirstOrDefault(method => !method.IsStatic && method.Parameters.Length == 0);
            if (direct != null)
                return direct;

            var special = isAsync
                ? compilation.GetTypeByMetadataName("System.IAsyncDisposable")
                : compilation.GetSpecialType(SpecialType.System_IDisposable);
            if (special is null || special.TypeKind == TypeKind.Error)
                return null;
            var interfaceMethod = special.GetMembers(name).OfType<IMethodSymbol>()
                .FirstOrDefault(method => method.Parameters.Length == 0);
            if (interfaceMethod == null)
                return null;
            return namedType.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol ?? interfaceMethod;
        }

        private void AddDeconstruction(string ownerId, DeconstructionInfo info, SyntaxNode syntax)
        {
            AddCallLike(ownerId, info.Method, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            foreach (var nested in info.Nested)
                AddDeconstruction(ownerId, nested, syntax);
        }

        private void AddAttributeConstructions()
        {
            foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
            {
                AddAttributes(type);
                foreach (var member in type.GetMembers())
                {
                    if (member is INamedTypeSymbol || member.IsImplicitlyDeclared)
                        continue;
                    AddAttributes(member);
                }
            }
        }

        private void AddAttributes(ISymbol symbol)
        {
            if (symbol.IsImplicitlyDeclared)
                return;
            if (DocId(symbol) is not { } sourceId)
                return;

            var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            AddAttributes(symbol.GetAttributes(), sourceId, syntax);

            // B33: parameter and return-value attributes belong to the declaring
            // member (an indexer parameter to the indexer).
            switch (symbol)
            {
                case IMethodSymbol method:
                    foreach (var parameter in method.Parameters)
                        AddAttributes(parameter.GetAttributes(), sourceId, syntax);
                    AddAttributes(method.GetReturnTypeAttributes(), sourceId, syntax);
                    break;
                case IPropertySymbol { IsIndexer: true } indexer:
                    foreach (var parameter in indexer.Parameters)
                        AddAttributes(parameter.GetAttributes(), sourceId, syntax);
                    break;
            }
        }

        private void AddAttributes(IEnumerable<AttributeData> attributes, string sourceId, SyntaxNode? syntax)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeClass is not { } attributeClass)
                    continue;
                if (DocId(attributeClass) is not { } targetId)
                    continue;
                Add(sourceId, nameof(Lurp.Storage.EdgeKind.Constructs), targetId, syntax);
                if (attribute.AttributeConstructor is { IsImplicitlyDeclared: false } attributeConstructor &&
                    DocId(attributeConstructor) is { } constructorId && constructorId != sourceId)
                    Add(sourceId, nameof(Lurp.Storage.EdgeKind.Calls), constructorId, syntax);
            }
        }

        private void Add(string source, string kind, string target, SyntaxNode? syntax)
        {
            _ = syntax;
            if (!declared.Contains(source) || !declared.Contains(target))
                return;
            facts.Add((source, kind, target));
        }

        internal static string? DocId(ISymbol symbol)
        {
            if (symbol is IMethodSymbol { ReducedFrom: not null } reduced)
                symbol = reduced.ReducedFrom;
            symbol = symbol.OriginalDefinition;
            symbol = NormalizeExtensionMember(symbol);
            return symbol.GetDocumentationCommentId();
        }

        // The extractor's SymbolIdFactory.Make normalizes in this order:
        // un-reduce, OriginalDefinition, then the extension-block mapping. This
        // oracle uses the same Roslyn API (AssociatedExtensionImplementation) and
        // never calls into Lurp.Shared.
        private static ISymbol NormalizeExtensionMember(ISymbol member)
        {
            if (member.ContainingType is not { IsExtension: true })
                return member;

            if (member is IMethodSymbol method)
                return method.AssociatedExtensionImplementation?.OriginalDefinition ?? method;

            return member;
        }

    }
}
