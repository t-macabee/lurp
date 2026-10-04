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
///     initializers, so no root list is shared with the extractor. The reviewed
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
                        AddPrimaryBase(primaryOwner, primaryOwnerId, primaryBase, model);
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

            // Probe inside the executable body: GetEnclosingSymbol resolves through
            // the enclosing binder, which for a declaration only exists inside the
            // body, not on the signature.
            var position = ProbePosition(node);
            var owner = model.GetEnclosingSymbol(position, CancellationToken.None);
            if (owner == null)
                return null;

            while (owner is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction } nested)
                owner = nested.ContainingSymbol;

            // An auto-property initializer's enclosing symbol is the compiler
            // backing field; extraction attributes it to the property.
            if (owner is IFieldSymbol { AssociatedSymbol: IPropertySymbol associatedProperty })
                owner = associatedProperty;

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
                        break;
                    case IObjectCreationOperation creation:
                        if (operation.Parent is IInterpolatedStringHandlerCreationOperation)
                            break;
                        if (creation.Type is { } createdType &&
                            DocId(createdType) is { } createdId && createdId != ownerId)
                            Add(ownerId, nameof(Lurp.Storage.EdgeKind.Constructs), createdId, operation.Syntax);
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
                    case IForEachLoopOperation loopOperation when loopOperation.Syntax is CommonForEachStatementSyntax loopSyntax:
                        AddForeach(ownerId, loopOperation.IsAsynchronous, loopSyntax, model);
                        break;
                    case IUsingDeclarationOperation usingDeclaration:
                        AddUsingDispose(ownerId, usingDeclaration.DeclarationGroup, usingDeclaration.IsAsynchronous, operation.Syntax, model);
                        break;
                    case IUsingOperation usingOperation:
                        AddUsingDispose(ownerId, usingOperation.Resources, usingOperation.IsAsynchronous, operation.Syntax, model);
                        break;
                    case IDeconstructionAssignmentOperation deconstruction when deconstruction.Syntax is AssignmentExpressionSyntax assignment:
                        AddDeconstruction(ownerId, deconstruction, assignment, model);
                        break;
                    case IPropertyReferenceOperation propertyReference:
                        AddMemberAccess(ownerId, propertyReference.Property, propertyReference);
                        break;
                    case IFieldReferenceOperation fieldReference:
                        AddMemberAccess(ownerId, fieldReference.Field, fieldReference);
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
                    case IConversionOperation conversion when conversion.OperatorMethod != null:
                        AddCall(ownerId, conversion.OperatorMethod, operation.Syntax, model);
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
                IAssignmentOperation assignment when ReferenceEquals(assignment.Target, operation) => (false, true),
                ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, operation) => (true, true),
                IIncrementOrDecrementOperation increment when ReferenceEquals(increment.Target, operation) => (true, true),
                IArgumentOperation argument when argument.Parameter is { RefKind: RefKind.Ref or RefKind.Out } => (false, true),
                _ => (true, false)
            };
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

        private void AddPrimaryBase(ISymbol owner, string ownerId, PrimaryConstructorBaseTypeSyntax primaryBase, SemanticModel model)
        {
            if (model.GetSymbolInfo(primaryBase).Symbol is IMethodSymbol resolved)
            {
                AddCallLike(ownerId, resolved, nameof(Lurp.Storage.EdgeKind.Calls), primaryBase);
                return;
            }

            if (owner is not IMethodSymbol { ContainingType.BaseType: INamedTypeSymbol baseType })
                return;

            var argumentTypes = primaryBase.ArgumentList.Arguments
                .Select(argument => model.GetTypeInfo(argument.Expression).Type)
                .ToArray();
            var csharpCompilation = (CSharpCompilation)compilation;
            foreach (var candidate in baseType.InstanceConstructors)
            {
                if (candidate.Parameters.Length != argumentTypes.Length)
                    continue;
                var matches = true;
                for (var i = 0; i < argumentTypes.Length; i++)
                {
                    if (argumentTypes[i] == null ||
                        !csharpCompilation.ClassifyConversion(argumentTypes[i]!, candidate.Parameters[i].Type).IsImplicit)
                    {
                        matches = false;
                        break;
                    }
                }

                if (!matches)
                    continue;
                AddCallLike(ownerId, candidate, nameof(Lurp.Storage.EdgeKind.Calls), primaryBase);
                break;
            }
        }

        private void AddAwait(string ownerId, AwaitExpressionSyntax syntax, SemanticModel model)
        {
            var awaitedType = model.GetTypeInfo(syntax.Expression).Type;
            if (awaitedType == null)
                return;
            var getAwaiter = FindParameterless(awaitedType, "GetAwaiter");
            if (getAwaiter == null)
                return;

            AddCallLike(ownerId, getAwaiter, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            var awaiterType = getAwaiter.ReturnType;
            AddCallLike(ownerId, FindParameterless(awaiterType, "GetResult"), nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            if (FindProperty(awaiterType, "IsCompleted") is { } isCompleted &&
                DocId(isCompleted) is { } isCompletedId)
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Reads), isCompletedId, syntax);
        }

        private void AddForeach(string ownerId, bool isAsync, CommonForEachStatementSyntax syntax, SemanticModel model)
        {
            var collectionType = model.GetTypeInfo(syntax.Expression).Type;
            if (collectionType == null)
                return;

            var getEnumerator = FindParameterless(collectionType, isAsync ? "GetAsyncEnumerator" : "GetEnumerator");
            if (getEnumerator == null)
            {
                var enumerable = isAsync
                    ? FindInterface(collectionType, static iface => iface.MetadataName == "IAsyncEnumerable`1")
                    : FindInterface(collectionType, static iface => iface.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                      ?? FindInterface(collectionType, static iface => iface.SpecialType == SpecialType.System_Collections_IEnumerable);
                if (enumerable != null)
                    getEnumerator = FindInterfaceMember(collectionType, enumerable, isAsync ? "GetAsyncEnumerator" : "GetEnumerator");
            }

            if (getEnumerator == null)
                return;

            AddCallLike(ownerId, getEnumerator, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            var enumeratorType = getEnumerator.ReturnType;
            AddCallLike(ownerId, FindParameterless(enumeratorType, isAsync ? "MoveNextAsync" : "MoveNext")
                       ?? FindEnumeratorInterfaceMember(enumeratorType, isAsync, "MoveNext") as IMethodSymbol,
                nameof(Lurp.Storage.EdgeKind.Calls), syntax);
            var current = FindProperty(enumeratorType, "Current")
                          ?? FindEnumeratorInterfaceMember(enumeratorType, isAsync, "Current") as IPropertySymbol;
            if (current != null && DocId(current) is { } currentId)
                Add(ownerId, nameof(Lurp.Storage.EdgeKind.Reads), currentId, syntax);
            AddCallLike(ownerId, FindParameterless(enumeratorType, isAsync ? "DisposeAsync" : "Dispose"),
                nameof(Lurp.Storage.EdgeKind.Calls), syntax);
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

        private IMethodSymbol? ResolveDispose(ITypeSymbol type, bool isAsync)
        {
            if (type is not INamedTypeSymbol namedType)
                return null;
            var name = isAsync ? "DisposeAsync" : "Dispose";
            if (FindParameterless(namedType, name) is { } direct)
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

        private void AddDeconstruction(string ownerId, IDeconstructionAssignmentOperation deconstruction, AssignmentExpressionSyntax syntax, SemanticModel model)
        {
            _ = model;
            var valueType = deconstruction.Value.Type;
            if (valueType == null)
                return;
            var count = deconstruction.Target switch
            {
                ITupleOperation tuple => tuple.Elements.Length,
                IDeclarationExpressionOperation
                {
                    Syntax: DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax designation }
                } => designation.Variables.Count,
                _ => 0
            };
            if (count == 0)
                return;
            var method = valueType.GetMembers("Deconstruct").OfType<IMethodSymbol>()
                .FirstOrDefault(candidate => !candidate.IsStatic &&
                                             candidate.Parameters.Length == count &&
                                             candidate.Parameters.All(parameter => parameter.RefKind == RefKind.Out));
            AddCallLike(ownerId, method, nameof(Lurp.Storage.EdgeKind.Calls), syntax);
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
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass is not { } attributeClass)
                    continue;
                if (DocId(attributeClass) is not { } targetId)
                    continue;
                var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
                Add(sourceId, nameof(Lurp.Storage.EdgeKind.Constructs), targetId, syntax);
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
            symbol = NormalizeExtensionMember(symbol);
            if (symbol is IMethodSymbol { ReducedFrom: not null } reduced)
                symbol = reduced.ReducedFrom;
            return symbol.OriginalDefinition.GetDocumentationCommentId();
        }

        private static ISymbol NormalizeExtensionMember(ISymbol member)
        {
            if (member.ContainingType is not { IsExtension: true, ContainingType: { } outer })
                return member;
            switch (member)
            {
                case IMethodSymbol method:
                    var unreduced = method.ReducedFrom ?? method;
                    var extensionParameter = method.ContainingType.ExtensionParameter;
                    foreach (var candidate in outer.GetMembers(method.Name).OfType<IMethodSymbol>())
                    {
                        if (candidate.Parameters.Length != unreduced.Parameters.Length + 1)
                            continue;
                        if (extensionParameter != null &&
                            !SymbolEqualityComparer.Default.Equals(candidate.Parameters[0].Type, extensionParameter.Type))
                            continue;
                        if (candidate.Parameters.Skip(1).Zip(unreduced.Parameters)
                            .All(pair => SymbolEqualityComparer.Default.Equals(pair.First.Type, pair.Second.Type)))
                            return candidate;
                    }

                    break;
                case IPropertySymbol property:
                    foreach (var candidate in outer.GetMembers(property.Name).OfType<IPropertySymbol>())
                    {
                        if (SymbolEqualityComparer.Default.Equals(candidate.Type, property.Type))
                            return candidate;
                    }

                    break;
            }

            return member;
        }

        private static IMethodSymbol? FindParameterless(ITypeSymbol? type, string name)
        {
            return type is INamedTypeSymbol namedType
                ? namedType.GetMembers(name).OfType<IMethodSymbol>()
                    .FirstOrDefault(method => !method.IsStatic && method.Parameters.Length == 0)
                : null;
        }

        private static IPropertySymbol? FindProperty(ITypeSymbol? type, string name)
        {
            return type is INamedTypeSymbol namedType
                ? namedType.GetMembers(name).OfType<IPropertySymbol>()
                    .FirstOrDefault(property => !property.IsStatic && property.Parameters.Length == 0)
                : null;
        }

        private static INamedTypeSymbol? FindInterface(ITypeSymbol type, Func<INamedTypeSymbol, bool> predicate)
        {
            if (type is not INamedTypeSymbol namedType)
                return null;
            return predicate(namedType) ? namedType : namedType.AllInterfaces.FirstOrDefault(predicate);
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
                ? FindInterface(enumeratorType, static iface => iface.MetadataName == "IAsyncEnumerator`1")
                : FindInterface(enumeratorType, static iface => iface.SpecialType == SpecialType.System_Collections_Generic_IEnumerator_T)
                  ?? FindInterface(enumeratorType, static iface => iface.SpecialType == SpecialType.System_Collections_IEnumerator);
            return interfaceType == null ? null : FindInterfaceMember(enumeratorType, interfaceType, name);
        }
    }
}
