using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class EfCoreAdapter : IFrameworkAdapter
{
    private const string DbContextMetadataName = "Microsoft.EntityFrameworkCore.DbContext";
    private const string DbSetMetadataName = "Microsoft.EntityFrameworkCore.DbSet`1";
    private const string EntityTypeConfigMetadataName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";
    private const string ModelBuilderMetadataName = "Microsoft.EntityFrameworkCore.ModelBuilder";
    private const string IndexBuilderMetadataName = "Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder";
    private const string IndexBuilderGenericMetadataName = "Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder`1";

    public string Name => "EF Core";
    public string Version => "efcore-v4";
    public string Description => "Entity Framework Core edges (DbSets, entity mappings)";

    /// <remarks>
    ///     Honors <see cref="AdapterExtractionContext.ScopeDocuments" /> like the other
    ///     five adapters: each walk is guarded by the declaring scope of the type it is
    ///     anchored to (the <c>DbContext</c> or <c>IEntityTypeConfiguration&lt;T&gt;</c>
    ///     class). Annotations carry the evidence document of the walk that produced
    ///     them, and <c>SqliteIndexStore.DeleteFactsByDocumentPaths</c> retires
    ///     copied-forward rows over exactly the extraction scope, so extraction and
    ///     deletion narrow in lockstep.
    /// </remarks>
    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var allTypes = context.GetAllNamedTypes().ToList();
        var ctx = new EfEmitContext(context, Version, [], []);

        ExtractDbContextMappings(allTypes, ctx);
        ExtractEntityTypeConfigurations(allTypes, ctx);

        return new AdapterExtractionResult(ctx.Edges, ctx.Annotations);
    }

    private sealed record EfEmitContext(
        AdapterExtractionContext Adapter,
        string ExtractorVersion,
        List<EdgeRecord> Edges,
        List<AnnotationRecord> Annotations)
    {
        internal Dictionary<AssemblyIdentity, INamedTypeSymbol[]> ScannableConfigurations { get; } = [];
    }

    private sealed class ModelMappings
    {
        internal ModelMappings(AdapterExtractionContext adapter)
        {
            TypeIds = new TypeIdComparer(adapter);
            IgnoredTypes = new HashSet<INamedTypeSymbol>(TypeIds);
            var members = new TypeMemberComparer(TypeIds);
            IgnoredProperties = new HashSet<(INamedTypeSymbol Type, string? Name)>(members);
            QueryFilters = new Dictionary<(INamedTypeSymbol Type, string? Name), string>(members);
        }

        /// <summary>
        ///     One key cache for the whole walk: every comparer of this walk and its emission shares it.
        /// </summary>
        internal TypeIdComparer TypeIds { get; }

        internal List<Mapping> Explicit { get; } = [];
        internal HashSet<INamedTypeSymbol> IgnoredTypes { get; }
        internal HashSet<(INamedTypeSymbol Type, string? Name)> IgnoredProperties { get; }
        internal Dictionary<(INamedTypeSymbol Type, string? Name), string> QueryFilters { get; }
    }

    private readonly record struct Mapping(
        INamedTypeSymbol Type,
        string Source,
        (string? path, int? sl, int? sc, int? el, int? ec) Location,
        INamedTypeSymbol? NavigationOwner = null,
        string? NavigationName = null);

    private sealed class TypeIdComparer(AdapterExtractionContext adapter) : IEqualityComparer<INamedTypeSymbol>
    {
        private readonly Dictionary<ITypeSymbol, string> _keys = new(SymbolEqualityComparer.Default);

        public bool Equals(INamedTypeSymbol? x, INamedTypeSymbol? y)
            => x != null && y != null && Key(x) == Key(y);
        public int GetHashCode(INamedTypeSymbol item)
            => Key(item).GetHashCode(StringComparison.Ordinal);

        private string Key(ITypeSymbol type)
        {
            if (_keys.TryGetValue(type, out var key))
                return key;

            key = ComputeKey(type);
            _keys[type] = key;
            return key;
        }

        private string ComputeKey(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
                return $"array{array.Rank}[{Key(array.ElementType)}]";
            if (type is not INamedTypeSymbol named)
                return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            var id = adapter.MakeSymbolId(named) ?? named.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var owner = named.ContainingType == null ? "" : Key(named.ContainingType);
            var arguments = string.Concat(named.TypeArguments.Select(argument =>
            {
                var key = Key(argument);
                return $"{key.Length}:{key}";
            }));
            return $"{id.Length}:{id}{owner.Length}:{owner}{arguments}";
        }
    }

    private sealed class TypeMemberComparer(TypeIdComparer types) : IEqualityComparer<(INamedTypeSymbol Type, string? Name)>
    {
        public bool Equals((INamedTypeSymbol Type, string? Name) x, (INamedTypeSymbol Type, string? Name) y)
            => types.Equals(x.Type, y.Type) && StringComparer.Ordinal.Equals(x.Name, y.Name);

        public int GetHashCode((INamedTypeSymbol Type, string? Name) item)
            => HashCode.Combine(types.GetHashCode(item.Type), item.Name == null ? 0 : StringComparer.Ordinal.GetHashCode(item.Name));
    }

    private static void ExtractDbContextMappings(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || ContainsTypeParameter(type) ||
                !FrameworkTypes.DerivesFrom(type, DbContextMetadataName))
                continue;

            if (!ctx.Adapter.IsSymbolInScope(type) || ctx.Adapter.MakeSymbolId(type) is not { } dbContextId)
                continue;

            var anchorLocation = ctx.Adapter.LocationResolver.Resolve(type);
            var mappings = new ModelMappings(ctx.Adapter);
            ExtractDbSetProperties(type, dbContextId, anchorLocation, ctx, mappings);

            for (var current = type; current != null && !FrameworkTypes.Is(current, DbContextMetadataName); current = current.BaseType)
            {
                var method = current.GetMembers("OnModelCreating").OfType<IMethodSymbol>().FirstOrDefault(OverridesOnModelCreating);
                if (method == null)
                    continue;

                WalkRootMethod(method, type, dbContextId, anchorLocation, configEntityType: null, ctx, mappings);
                break;
            }

            EmitMappings(mappings, dbContextId, anchorLocation, ctx, expandNavigations: true);
        }
    }

    private static bool OverridesOnModelCreating(IMethodSymbol method)
    {
        for (var current = method.OverriddenMethod; current != null; current = current.OverriddenMethod)
        {
            if (current.Name == "OnModelCreating" && FrameworkTypes.Is(current.ContainingType, DbContextMetadataName))
                return true;
        }

        return false;
    }

    private static void ExtractDbSetProperties(
        INamedTypeSymbol type,
        string dbContextId,
        (string? path, int? sl, int? sc, int? el, int? ec) anchorLocation,
        EfEmitContext ctx,
        ModelMappings mappings)
    {
        for (var current = type;
             current != null && !FrameworkTypes.Is(current, DbContextMetadataName);
             current = current.BaseType)
        {
            var inherited = !SymbolEqualityComparer.Default.Equals(current, type);
            foreach (var prop in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (prop.IsStatic || prop.IsIndexer ||
                    (inherited && prop.DeclaredAccessibility == Accessibility.Private) ||
                    prop.Type is not INamedTypeSymbol propType ||
                    !FrameworkTypes.Is(propType, DbSetMetadataName) ||
                    propType.TypeArguments is not [INamedTypeSymbol entityType])
                    continue;

                mappings.Explicit.Add(new Mapping(entityType, dbContextId, anchorLocation));
                if (ContainsTypeParameter(prop.OriginalDefinition.Type) ||
                    !ctx.Adapter.IsSolutionSymbol(prop) || ctx.Adapter.MakeSymbolId(prop) is not { } propId)
                    continue;

                var location = inherited ? anchorLocation : ctx.Adapter.LocationResolver.Resolve(prop);
                mappings.Explicit.Add(new Mapping(entityType, propId, location));
            }
        }
    }

    private static void ExtractEntityTypeConfigurations(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (!IsConfigurationType(type) || !ctx.Adapter.IsSymbolInScope(type) ||
                !HasConfigurationInterface(type) || ctx.Adapter.MakeSymbolId(type) is not { } configId)
                continue;

            var anchorLocation = ctx.Adapter.LocationResolver.Resolve(type);
            var mappings = new ModelMappings(ctx.Adapter);
            foreach (var (entity, configure) in ConfigurationEntries(type))
            {
                mappings.Explicit.Add(new Mapping(entity, configId, anchorLocation));
                if (configure != null)
                    WalkRootMethod(configure, type, configId, anchorLocation, entity, ctx, mappings);
            }

            EmitMappings(mappings, configId, anchorLocation, ctx, expandNavigations: false);
        }
    }

    private static IEnumerable<(INamedTypeSymbol Entity, IMethodSymbol? Configure)> ConfigurationEntries(
        INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (!FrameworkTypes.Is(iface, EntityTypeConfigMetadataName) ||
                iface.TypeArguments is not [INamedTypeSymbol entity] || ContainsTypeParameter(entity))
                continue;

            var member = iface.GetMembers("Configure").OfType<IMethodSymbol>().FirstOrDefault();
            var implementation = member == null ? null : type.FindImplementationForInterfaceMember(member) as IMethodSymbol;
            yield return (entity, implementation == null ? null : MostDerivedOverride(type, implementation));
        }
    }

    private static IMethodSymbol MostDerivedOverride(INamedTypeSymbol type, IMethodSymbol method)
    {
        for (var current = type; current != null; current = current.BaseType)
            foreach (var candidate in current.GetMembers(method.Name).OfType<IMethodSymbol>())
                for (var overridden = candidate.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                    if (SymbolEqualityComparer.Default.Equals(overridden.OriginalDefinition, method.OriginalDefinition))
                        return candidate;
        return method;
    }

    private static bool HasConfigurationInterface(INamedTypeSymbol type)
        => type.AllInterfaces.Any(iface => FrameworkTypes.Is(iface, EntityTypeConfigMetadataName));

    private static bool IsConfigurationType(INamedTypeSymbol type)
        => type is { TypeKind: TypeKind.Class or TypeKind.Struct, IsAbstract: false, IsRefLikeType: false };

    private static bool HasParameterlessConstructor(INamedTypeSymbol type)
    {
        // Reflection does not see the implicit default constructor of a struct.
        return type.InstanceConstructors.Any(ctor => ctor.Parameters.IsEmpty &&
                                                    (!type.IsValueType || !ctor.IsImplicitlyDeclared));
    }

    private static bool ContainsTypeParameter(ITypeSymbol? type)
    {
        return type switch
        {
            ITypeParameterSymbol => true,
            IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
            INamedTypeSymbol named => named.IsUnboundGenericType ||
                                      named.TypeArguments.Any(ContainsTypeParameter) ||
                                      ContainsTypeParameter(named.ContainingType),
            _ => false
        };
    }

    private static void WalkRootMethod(
        IMethodSymbol method,
        INamedTypeSymbol anchorType,
        string anchorId,
        (string? path, int? sl, int? sc, int? el, int? ec) anchorLocation,
        INamedTypeSymbol? configEntityType,
        EfEmitContext ctx,
        ModelMappings mappings)
    {
        var walk = new ModelWalk(anchorType, anchorId, anchorLocation, ctx, mappings, configEntityType == null);
        walk.VisitMethod(method, configEntityType, builderArguments: null,
            mappingsOnly: false, onContextInstance: configEntityType == null, dispatchType: anchorType);
    }

    private sealed class ModelWalk(
        INamedTypeSymbol anchorType,
        string anchorId,
        (string? path, int? sl, int? sc, int? el, int? ec) anchorLocation,
        EfEmitContext ctx,
        ModelMappings mappings,
        bool contextRoot)
    {
        private readonly Dictionary<IMethodSymbol, HashSet<INamedTypeSymbol?>> _activeMethods =
            new(SymbolEqualityComparer.Default);

        internal void VisitMethod(
            IMethodSymbol method,
            INamedTypeSymbol? configEntity,
            IReadOnlyDictionary<int, INamedTypeSymbol>? builderArguments,
            bool mappingsOnly,
            bool onContextInstance,
            INamedTypeSymbol dispatchType)
        {
            method = method.PartialImplementationPart ?? method;
            // Use definitions to bound recursion even if a helper constructs a deeper generic type on each call.
            var definition = method.OriginalDefinition;
            var entity = (configEntity ?? builderArguments?.Values.FirstOrDefault() ??
                method.TypeArguments.OfType<INamedTypeSymbol>().FirstOrDefault())?.OriginalDefinition;
            if (!_activeMethods.TryGetValue(definition, out var entities))
                _activeMethods[definition] = entities = new HashSet<INamedTypeSymbol?>(SymbolEqualityComparer.Default);
            if (!entities.Add(entity))
                return;

            foreach (var syntaxRef in method.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax();
                var model = ctx.Adapter.GetDeclaringSemanticModel(syntax.SyntaxTree);
                if (model?.GetOperation(syntax) is not { } root ||
                    model.GetDeclaredSymbol(syntax) is not IMethodSymbol declaration)
                    continue;

                var body = new MethodBody(method, declaration, ctx.Adapter.Compilation, configEntity, builderArguments,
                    onContextInstance, dispatchType);
                var walker = new InvocationWalker(invocation => VisitInvocation(invocation, body, mappingsOnly));
                walker.Visit(root is ILocalFunctionOperation local ? local.Body : root);
            }

            entities.Remove(entity);
        }

        private void VisitInvocation(IInvocationOperation invocation, MethodBody body, bool mappingsOnly)
        {
            var target = invocation.TargetMethod;
            var declaredInAnchor = SymbolEqualityComparer.Default.Equals(body.Method.ContainingType, anchorType);
            var location = !mappingsOnly && declaredInAnchor
                ? ctx.Adapter.LocationResolver.Resolve(invocation.Syntax.GetLocation())
                : anchorLocation;

            if (FollowSolutionCall(invocation, body, mappingsOnly))
                return;

            var rawType = target.TypeArguments.FirstOrDefault() ??
                invocation.Arguments.Select(a => UnwrapConversionAndDelegate(a.Value))
                    .OfType<ITypeOfOperation>().FirstOrDefault()?.TypeOperand;
            var explicitType = body.ResolveEntityType(rawType);

            var isModelBuilder = FrameworkTypes.Is(target.ContainingType, ModelBuilderMetadataName);
            if (isModelBuilder || IsEntityBuilderType(target.ContainingType))
                FollowBuilderMethodGroups(invocation, body, mappingsOnly,
                    GetModelBuilderEntity(invocation, body) ?? explicitType ??
                    GetBuilderEntity(InvocationReceiver(invocation), body));

            if (isModelBuilder)
            {
                if (contextRoot && target.Name == "Entity" && explicitType != null)
                    mappings.Explicit.Add(new Mapping(explicitType, anchorId, location));
                if (target.Name == "Ignore" && explicitType != null)
                    mappings.IgnoredTypes.Add(explicitType);
                if (contextRoot && !mappingsOnly)
                    ApplyConfigurations(invocation, body);
                return;
            }

            var builder = target.ContainingType;
            var entityBuilder = IsBuilder(builder, "EntityTypeBuilder", 1);
            if (!entityBuilder && !IsEntityBuilderType(builder))
                return;

            var builderEntity = body.ResolveEntityType(builder.TypeArguments.LastOrDefault()) ??
                                GetBuilderEntity(invocation.Instance, body);

            if (target.Name is "OwnsOne" or "OwnsMany" or "ComplexProperty" or "ComplexCollection")
            {
                var property = FindConfiguredProperty(invocation, builderEntity);
                var mapped = explicitType ?? NavigationType(body.ResolveType(property?.Type));
                if (mapped != null)
                {
                    mappings.Explicit.Add(new Mapping(mapped, anchorId, location, builderEntity,
                        property?.Name ?? ReadMemberName(invocation)));
                }
                return;
            }

            if (target.Name == "Ignore" && builderEntity != null)
            {
                var name = FindConfiguredProperty(invocation, builderEntity)?.Name ?? ReadMemberName(invocation);
                if (name != null)
                    mappings.IgnoredProperties.Add((builderEntity, name));
                return;
            }

            if (!entityBuilder || mappingsOnly)
                return;

            if (target.Name is "HasOne" or "HasMany" && target.TypeArguments is [var rawRel])
            {
                var rel = body.ResolveEntityType(rawRel);
                if (rel != null && ctx.Adapter.MakeSymbolId(rel) is { } relId)
                {
                    ctx.Edges.Add(ctx.Adapter.MakeEdge(anchorId, relId, nameof(EdgeKind.References),
                        Provenance.FrameworkDerived, ctx.ExtractorVersion, location));
                }

                return;
            }

            if (builderEntity == null)
                return;

            if (target.Name == "HasQueryFilter")
                EmitQueryFilter(invocation, builderEntity, mappings);
            else if (target.Name == "HasIndex" && ctx.Adapter.MakeSymbolId(builderEntity) is { } id)
                EmitUniqueIndexIfUnique(invocation, builderEntity, id, anchorLocation.path, ctx);
        }

        private void FollowBuilderMethodGroups(IInvocationOperation invocation, MethodBody body, bool mappingsOnly,
            INamedTypeSymbol? entity)
        {
            foreach (var argument in invocation.Arguments)
            {
                if (UnwrapConversionAndDelegate(argument.Value) is not IMethodReferenceOperation reference)
                    continue;
                var target = reference.Method.PartialImplementationPart ?? reference.Method;
                if (!ctx.Adapter.IsSolutionSymbol(target) || target.DeclaringSyntaxReferences.Length == 0)
                    continue;
                Dictionary<int, INamedTypeSymbol>? builderArguments = null;
                var hasBuilder = false;
                foreach (var parameter in target.Parameters)
                {
                    if (parameter.Type is not INamedTypeSymbol builder || !IsEntityBuilderType(builder))
                        continue;
                    hasBuilder = true;
                    var parameterEntity = body.ResolveEntityType(builder.TypeArguments.LastOrDefault()) ?? entity;
                    if (parameterEntity != null)
                        (builderArguments ??= [])[parameter.Ordinal] = parameterEntity;
                }
                if (!hasBuilder)
                    continue;
                var onContextInstance = body.OnContextInstance &&
                    (IsThisReference(reference.Instance) || target is { MethodKind: MethodKind.LocalFunction, IsStatic: false });
                // Roslyn leaves IMethodReferenceOperation.IsVirtual true for `base.` method groups.
                if (reference.IsVirtual && IsThisReference(reference.Instance) &&
                    reference.Syntax is not MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax })
                    target = MostDerivedOverride(body.DispatchType, target);
                VisitMethod(body.BindMethod(target), configEntity: null, builderArguments, mappingsOnly,
                    onContextInstance, body.DispatchType);
            }
        }

        private bool FollowSolutionCall(IInvocationOperation invocation, MethodBody body, bool mappingsOnly)
        {
            var target = invocation.TargetMethod.PartialImplementationPart ?? invocation.TargetMethod;
            if (!ctx.Adapter.IsSolutionSymbol(target) || target.DeclaringSyntaxReferences.Length == 0)
                return false;

            var passesBuilder = false;
            Dictionary<int, INamedTypeSymbol>? builderArguments = null;
            foreach (var argument in invocation.Arguments)
            {
                var value = UnwrapConversionAndDelegate(argument.Value);
                if (FrameworkTypes.Is(value.Type, ModelBuilderMetadataName))
                {
                    passesBuilder = true;
                }
                else if (value.Type is INamedTypeSymbol named && IsEntityBuilderType(named))
                {
                    passesBuilder = true;
                    if (argument.Parameter != null && GetBuilderEntity(value, body) is { } entity)
                        (builderArguments ??= [])[argument.Parameter.Ordinal] = entity;
                }
            }

            var callsBaseOverride = invocation.Syntax is InvocationExpressionSyntax
                { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } } &&
                OverridesOnModelCreating(target);
            if (!passesBuilder && !callsBaseOverride && target.MethodKind != MethodKind.LocalFunction)
                return false;

            var onContextInstance = body.OnContextInstance &&
                (IsThisReference(invocation.Instance) || target is { MethodKind: MethodKind.LocalFunction, IsStatic: false });
            if (invocation.IsVirtual && IsThisReference(invocation.Instance))
                target = MostDerivedOverride(body.DispatchType, target);
            VisitMethod(body.BindMethod(target), configEntity: null, builderArguments, mappingsOnly, onContextInstance, body.DispatchType);
            return true;
        }

        private void ApplyConfigurations(IInvocationOperation invocation, MethodBody body)
        {
            switch (invocation.TargetMethod.Name)
            {
                case "ApplyConfiguration":
                    if (invocation.Arguments is [var argument] &&
                        body.ResolveEntityType(UnwrapConversionAndDelegate(argument.Value).Type) is { } config &&
                        IsConfigurationType(config) &&
                        body.ResolveEntityType(invocation.TargetMethod.TypeArguments.FirstOrDefault()) is { } entity)
                    {
                        ApplyConfiguration(config, entity);
                    }

                    break;
                case "ApplyConfigurationsFromAssembly":
                    var predicate = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "predicate");
                    if (predicate != null && !IsNullOrDefault(predicate.Value))
                        return;

                    var assembly = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name == "assembly");
                    if (assembly != null && ResolveConfigurationAssembly(assembly.Value, body) is { } resolved)
                    {
                        foreach (var type in ScannableConfigurations(resolved))
                            ApplyConfiguration(type);
                    }

                    break;
            }
        }

        private INamedTypeSymbol[] ScannableConfigurations(IAssemblySymbol assembly)
        {
            if (!ctx.ScannableConfigurations.TryGetValue(assembly.Identity, out var types))
            {
                types = assembly.Identity.Equals(ctx.Adapter.Compilation.Assembly.Identity) ||
                        ctx.Adapter.Inputs.SolutionAssemblyIdentities?.Contains(assembly.Identity) == true
                    ? ExtractionUtils.GetAllNamedTypes(assembly.GlobalNamespace)
                        .Where(type => IsConfigurationType(type) && !ContainsTypeParameter(type) &&
                                       HasConfigurationInterface(type) && HasParameterlessConstructor(type))
                        .OrderBy(ExtractionUtils.FullMetadataName, StringComparer.Ordinal)
                        .ToArray()
                    : [];
                ctx.ScannableConfigurations.Add(assembly.Identity, types);
            }

            return types;
        }

        private IAssemblySymbol? ResolveConfigurationAssembly(IOperation expression, MethodBody body)
        {
            expression = UnwrapConversionAndDelegate(expression);
            if (expression is IInvocationOperation executing &&
                executing.TargetMethod.Name == "GetExecutingAssembly" &&
                FrameworkTypes.Is(executing.TargetMethod.ContainingType, "System.Reflection.Assembly"))
                return body.Method.ContainingAssembly;

            if (expression is not IPropertyReferenceOperation property || property.Property.Name != "Assembly" ||
                !FrameworkTypes.Is(property.Property.ContainingType, "System.Type") || property.Instance == null)
                return null;

            var receiver = UnwrapConversionAndDelegate(property.Instance);
            if (receiver is ITypeOfOperation typeOf)
            {
                var operand = body.ResolveType(typeOf.TypeOperand);
                while (operand is IArrayTypeSymbol array)
                    operand = array.ElementType;

                return (operand as INamedTypeSymbol)?.ContainingAssembly;
            }

            if (receiver is IInvocationOperation getType &&
                getType.TargetMethod is { Name: "GetType", ContainingType.SpecialType: SpecialType.System_Object } &&
                body.OnContextInstance && IsThisReference(getType.Instance))
                return anchorType.ContainingAssembly;

            return null;
        }

        private static bool IsThisReference(IOperation? operation)
            => operation is IInstanceReferenceOperation { ReferenceKind: InstanceReferenceKind.ContainingTypeInstance };

        private void ApplyConfiguration(INamedTypeSymbol type, INamedTypeSymbol? selectedEntity = null)
        {
            if (!ctx.Adapter.IsSolutionSymbol(type) || ctx.Adapter.MakeSymbolId(type) is not { } configId)
                return;

            foreach (var (entity, configure) in ConfigurationEntries(type))
            {
                if (selectedEntity != null && !SymbolEqualityComparer.Default.Equals(selectedEntity, entity))
                    continue;

                ctx.Edges.Add(ctx.Adapter.MakeEdge(anchorId, configId, nameof(EdgeKind.References),
                    Provenance.FrameworkDerived, ctx.ExtractorVersion, anchorLocation));
                mappings.Explicit.Add(new Mapping(entity, anchorId, anchorLocation));
                if (configure != null)
                    VisitMethod(configure, entity, builderArguments: null, mappingsOnly: true, onContextInstance: false, dispatchType: type);
            }
        }
    }

    private sealed class InvocationWalker(Action<IInvocationOperation> visit) : OperationWalker
    {
        public override void VisitInvocation(IInvocationOperation operation)
        {
            base.VisitInvocation(operation);
            visit(operation);
        }

        public override void VisitLocalFunction(ILocalFunctionOperation operation)
        {
            // A local function has its own body. Follow it where it is called.
        }
    }

    private sealed record MethodBody(
        IMethodSymbol Method,
        IMethodSymbol Declaration,
        Compilation Compilation,
        INamedTypeSymbol? ConfigurationEntity,
        IReadOnlyDictionary<int, INamedTypeSymbol>? BuilderArguments,
        bool OnContextInstance,
        INamedTypeSymbol DispatchType)
    {
        internal INamedTypeSymbol? ResolveEntityType(ITypeSymbol? candidate)
            => ResolveType(candidate) is INamedTypeSymbol { TypeKind: not TypeKind.Error } named &&
               !ContainsTypeParameter(named) ? named : null;

        internal ITypeSymbol? ResolveType(ITypeSymbol? candidate)
        {
            switch (candidate)
            {
                case ITypeParameterSymbol parameter:
                    if (parameter.TypeParameterKind == TypeParameterKind.Method &&
                        SymbolEqualityComparer.Default.Equals(parameter.DeclaringMethod, Declaration))
                        return Method.TypeArguments[parameter.Ordinal];

                    var boundType = Method.ContainingType;
                    for (var type = Declaration.ContainingType; type != null && boundType != null;
                         type = type.ContainingType, boundType = boundType.ContainingType)
                    {
                        if (SymbolEqualityComparer.Default.Equals(parameter.DeclaringType, type.OriginalDefinition))
                            return boundType.TypeArguments[parameter.Ordinal];
                    }

                    return parameter;
                case IArrayTypeSymbol array:
                    var element = ResolveType(array.ElementType)!;
                    return SymbolEqualityComparer.Default.Equals(element, array.ElementType)
                        ? array
                        : Compilation.CreateArrayTypeSymbol(element, array.Rank);
                case INamedTypeSymbol { IsUnboundGenericType: false } named when ContainsTypeParameter(named):
                    var definition = named.ConstructedFrom;
                    if (named.ContainingType != null &&
                        ResolveType(named.ContainingType) is INamedTypeSymbol owner &&
                        !SymbolEqualityComparer.Default.Equals(owner, named.ContainingType))
                    {
                        definition = owner.GetTypeMembers(named.Name, named.Arity)
                            .FirstOrDefault(type => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, named.OriginalDefinition))
                            ?? definition;
                    }

                    return named.Arity == 0 ? definition : definition.Construct(named.TypeArguments.Select(t => ResolveType(t)!).ToArray());
                default:
                    return candidate;
            }
        }

        internal IMethodSymbol BindMethod(IMethodSymbol target)
        {
            var method = target.ConstructedFrom;
            if (ResolveType(target.ContainingType) is INamedTypeSymbol owner &&
                !SymbolEqualityComparer.Default.Equals(owner, target.ContainingType))
            {
                method = owner.GetMembers(target.Name).OfType<IMethodSymbol>()
                    .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, target.OriginalDefinition))
                    ?? method;
            }

            return target.Arity == 0 ? method : method.Construct(target.TypeArguments.Select(t => ResolveType(t)!).ToArray());
        }

        internal INamedTypeSymbol? GetParameterEntity(IParameterSymbol parameter)
        {
            if (!SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, Declaration))
                return null;

            return BuilderArguments?.GetValueOrDefault(parameter.Ordinal) ?? ConfigurationEntity;
        }
    }

    private static bool IsBuilder(INamedTypeSymbol type, string name, int genericArity)
    {
        const string prefix = "Microsoft.EntityFrameworkCore.Metadata.Builders.";
        return FrameworkTypes.Is(type, prefix + name) ||
               FrameworkTypes.Is(type, $"{prefix}{name}`{genericArity}");
    }

    private static bool IsEntityBuilderType(INamedTypeSymbol type)
        => IsBuilder(type, "EntityTypeBuilder", 1) || IsBuilder(type, "OwnedNavigationBuilder", 2) ||
           IsBuilder(type, "ComplexPropertyBuilder", 1) || IsBuilder(type, "ComplexCollectionBuilder", 1);

    private static INamedTypeSymbol? GetBuilderEntity(IOperation? instance, MethodBody body)
    {
        if (instance?.Type is INamedTypeSymbol builder && IsEntityBuilderType(builder) &&
            body.ResolveEntityType(builder.TypeArguments.LastOrDefault()) is { } entity)
            return entity;

        if (instance is IParameterReferenceOperation parameter)
        {
            if (body.GetParameterEntity(parameter.Parameter) is { } parameterEntity)
                return parameterEntity;

            for (var current = instance.Parent; current != null; current = current.Parent)
            {
                if (current is not IAnonymousFunctionOperation lambda ||
                    !lambda.Symbol.Parameters.Any(candidate =>
                        SymbolEqualityComparer.Default.Equals(candidate, parameter.Parameter)))
                    continue;

                for (var outer = lambda.Parent; outer != null; outer = outer.Parent)
                {
                    if (outer is IInvocationOperation enclosing)
                        return GetModelBuilderEntity(enclosing, body);
                }

                return null;
            }
        }

        if (instance is not IInvocationOperation invocation)
            return null;

        return GetModelBuilderEntity(invocation, body) ?? GetBuilderEntity(InvocationReceiver(invocation), body);
    }

    private static INamedTypeSymbol? GetModelBuilderEntity(IInvocationOperation invocation, MethodBody body)
    {
        if (invocation.TargetMethod.Name != "Entity" ||
            !FrameworkTypes.Is(invocation.TargetMethod.ContainingType, ModelBuilderMetadataName))
            return null;

        return body.ResolveEntityType(invocation.TargetMethod.TypeArguments.FirstOrDefault() ??
            invocation.Arguments.Select(a => UnwrapConversionAndDelegate(a.Value))
                .OfType<ITypeOfOperation>().FirstOrDefault()?.TypeOperand);
    }

    /// <summary>
    ///     The expression an invocation was called on. Roslyn binds an extension method to its
    ///     unreduced form, so the receiver of a chained extension call such as
    ///     <c>e.ToTable("Roots")</c> sits in the first argument instead of
    ///     <see cref="IInvocationOperation.Instance" />.
    /// </summary>
    private static IOperation? InvocationReceiver(IInvocationOperation invocation)
    {
        if (invocation.Instance != null)
            return invocation.Instance;

        return invocation.TargetMethod is { IsExtensionMethod: true, ReducedFrom: null } &&
               invocation.Arguments.Length > 0
            ? UnwrapConversionAndDelegate(invocation.Arguments[0].Value)
            : null;
    }

    private static string? ReadMemberName(IInvocationOperation invocation)
    {
        return invocation.Arguments.FirstOrDefault(a => a.Parameter?.Name is "navigationName" or "propertyName")
            ?.Value.ConstantValue is { HasValue: true, Value: string name } ? name : null;
    }

    private static IPropertySymbol? FindConfiguredProperty(IInvocationOperation invocation, INamedTypeSymbol? builderEntity)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (UnwrapConversionAndDelegate(argument.Value) is IAnonymousFunctionOperation lambda &&
                lambda.Body.Operations.OfType<IReturnOperation>().FirstOrDefault()?.ReturnedValue is { } returned &&
                UnwrapConversionAndDelegate(returned) is IPropertyReferenceOperation property)
                return property.Property;
        }

        var name = ReadMemberName(invocation);
        for (var current = builderEntity; name != null && current != null; current = current.BaseType)
        {
            if (current.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault() is { } property)
                return property;
        }

        return null;
    }

    private static INamedTypeSymbol? NavigationType(ITypeSymbol? candidate)
    {
        if (candidate is IArrayTypeSymbol array)
            return array.ElementType as INamedTypeSymbol;
        if (candidate is not INamedTypeSymbol type || type.SpecialType != SpecialType.None)
            return null;

        var element = FrameworkTypes.Is(type, "System.Collections.Generic.IEnumerable`1")
            ? type
            : type.AllInterfaces.FirstOrDefault(i => FrameworkTypes.Is(i, "System.Collections.Generic.IEnumerable`1"));
        return element == null ? type : element.TypeArguments[0] as INamedTypeSymbol;
    }

    private static bool IsOwned(ISymbol symbol)
    {
        return FrameworkTypes.GetAttributesWithInheritance(symbol).Any(a =>
            FrameworkTypes.Is(a.AttributeClass, "Microsoft.EntityFrameworkCore.OwnedAttribute"));
    }

    private static bool IsNotMapped(ISymbol symbol)
    {
        return FrameworkTypes.GetAttributesWithInheritance(symbol).Any(a =>
            FrameworkTypes.Is(a.AttributeClass, "System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute"));
    }

    private static void EmitMappings(
        ModelMappings mappings,
        string anchorId,
        (string? path, int? sl, int? sc, int? el, int? ec) anchorLocation,
        EfEmitContext ctx,
        bool expandNavigations)
    {
        foreach (var ((type, _), value) in mappings.QueryFilters)
        {
            if (ctx.Adapter.MakeSymbolId(type) is { } id)
                AddConstraintAnnotation(id, "ef_query_filter_constraint", value, anchorLocation.path, ctx);
        }

        var mappedTypes = new HashSet<INamedTypeSymbol>(mappings.TypeIds);
        var expandedDefinitions = new HashSet<INamedTypeSymbol>(mappings.TypeIds);
        var queue = new Queue<INamedTypeSymbol>();

        foreach (var mapping in mappings.Explicit)
        {
            var type = mapping.Type;
            if (ContainsTypeParameter(type) || mappings.IgnoredTypes.Contains(type) || !ctx.Adapter.IsSolutionSymbol(type) ||
                ctx.Adapter.MakeSymbolId(type) is not { } id)
                continue;

            if (mapping.NavigationOwner is { } owner &&
                (mappings.IgnoredTypes.Contains(owner) || mappings.IgnoredProperties.Contains((owner, mapping.NavigationName))))
                continue;

            AddMapsToEdge(mapping.Source, id, ctx, mapping.Location);
            if (expandNavigations && mappedTypes.Add(type))
            {
                expandedDefinitions.Add(type.OriginalDefinition);
                queue.Enqueue(type);
            }
        }

        while (queue.TryDequeue(out var type))
        {
            var hiddenProperties = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!hiddenProperties.Add(property.Name) || property.IsStatic || property.IsIndexer ||
                        property.GetMethod?.DeclaredAccessibility != Accessibility.Public ||
                        mappings.IgnoredProperties.Contains((current, property.Name)) ||
                        mappings.IgnoredProperties.Contains((type, property.Name)) || IsNotMapped(property))
                        continue;

                    var target = NavigationType(property.Type);
                    if (target is not { TypeKind: TypeKind.Class } || !ctx.Adapter.IsSolutionSymbol(target) ||
                        (property.SetMethod == null && !IsOwned(target) &&
                         SymbolEqualityComparer.Default.Equals(property.Type, target)) ||
                        ContainsTypeParameter(target) || mappings.IgnoredTypes.Contains(target) || IsNotMapped(target) ||
                        ctx.Adapter.MakeSymbolId(target) is not { } targetId || !mappedTypes.Add(target))
                        continue;

                    AddMapsToEdge(anchorId, targetId, ctx, anchorLocation);
                    // A recursive property can construct the same definition with deeper type arguments at each hop.
                    if (expandedDefinitions.Add(target.OriginalDefinition))
                        queue.Enqueue(target);
                }
            }
        }
    }

    private static void EmitQueryFilter(
        IInvocationOperation invocation,
        INamedTypeSymbol entityType,
        ModelMappings mappings)
    {
        if (invocation.Arguments.Length is not (1 or 2))
            return;

        var key = invocation.Arguments.Length == 2
            ? invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0)?.Value
            : null;
        var filter = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == invocation.Arguments.Length - 1)?.Value;
        if (filter == null)
            return;

        var filterKey = key == null ? null : key.ConstantValue is { Value: string name } ? name : key.Syntax.ToString();
        if (IsNullOrDefault(filter))
        {
            mappings.QueryFilters.Remove((entityType, filterKey));
            return;
        }

        var label = filterKey == null ? "HasQueryFilter" : $"HasQueryFilter {filterKey}";
        mappings.QueryFilters[(entityType, filterKey)] = $"{entityType.Name}: {label}: {filter.Syntax}";
    }

    private static bool IsNullOrDefault(IOperation value)
    {
        value = UnwrapConversionAndDelegate(value);
        return value is IDefaultValueOperation || value.ConstantValue is { HasValue: true, Value: null };
    }

    private static void EmitUniqueIndexIfUnique(
        IInvocationOperation hasIndexOp,
        INamedTypeSymbol entityType,
        string entityTypeId,
        string? evidencePath,
        EfEmitContext ctx)
    {
        var isUnique = false;
        string? databaseName = null;

        IOperation current = hasIndexOp;
        while (current.Parent != null)
        {
            if (current.Parent is IConversionOperation conversion)
            {
                current = conversion;
                continue;
            }

            IInvocationOperation? nextInvocation = null;
            if (current.Parent is IInvocationOperation instanceInv && ReferenceEquals(instanceInv.Instance, current))
            {
                nextInvocation = instanceInv;
            }
            else if (current.Parent is IArgumentOperation arg &&
                     arg.Parent is IInvocationOperation extInv &&
                     extInv.TargetMethod.IsExtensionMethod &&
                     extInv.Arguments.Length > 0 &&
                     ReferenceEquals(extInv.Arguments[0], arg))
            {
                nextInvocation = extInv;
            }

            if (nextInvocation == null)
                break;

            var nextMethod = nextInvocation.TargetMethod;
            if (nextMethod.Name == "IsUnique" &&
                (FrameworkTypes.Is(nextMethod.ContainingType, IndexBuilderMetadataName) ||
                 FrameworkTypes.Is(nextMethod.ContainingType, IndexBuilderGenericMetadataName)))
            {
                if (nextInvocation.Arguments.Length == 0)
                    isUnique = true;
                else if (nextInvocation.Arguments.Length == 1 &&
                         nextInvocation.Arguments[0].Value.ConstantValue is { HasValue: true, Value: bool uniqueFlag })
                    isUnique = uniqueFlag;
                else
                    isUnique = false;
            }
            else if (nextMethod.Name == "HasDatabaseName" && nextInvocation.Arguments.Length >= 1)
            {
                var nameArg = nextInvocation.Arguments[^1].Value;
                if (nameArg.ConstantValue is { HasValue: true, Value: string dbName })
                    databaseName = dbName;
            }

            current = nextInvocation;
        }

        if (!isUnique)
            return;

        var properties = ExtractIndexProperties(hasIndexOp);
        var propsText = string.Join(", ", properties);
        var value = databaseName != null
            ? $"{entityType.Name}({propsText}) name={databaseName}"
            : $"{entityType.Name}({propsText})";

        AddConstraintAnnotation(
            entityTypeId,
            "ef_unique_index_constraint",
            value,
            evidencePath,
            ctx);
    }

    private static List<string> ExtractIndexProperties(IInvocationOperation hasIndexOp)
    {
        if (hasIndexOp.Arguments.Length == 0)
            return ["?"];

        var firstArg = UnwrapConversionAndDelegate(hasIndexOp.Arguments[0].Value);
        if (firstArg is IAnonymousFunctionOperation lambda)
            return ExtractLambdaProperties(lambda);

        var names = new List<string>();
        foreach (var arg in hasIndexOp.Arguments)
        {
            var val = UnwrapConversionAndDelegate(arg.Value);
            if (val is IArrayCreationOperation { Initializer: not null } arrayCreation)
            {
                foreach (var elem in arrayCreation.Initializer.ElementValues)
                    names.Add(elem.ConstantValue is { HasValue: true, Value: string s } ? s : "?");
            }
            else if (val is ICollectionExpressionOperation collectionExpr)
            {
                foreach (var elem in collectionExpr.Elements)
                    names.Add(elem.ConstantValue is { HasValue: true, Value: string s } ? s : "?");
            }
            else if (val.ConstantValue is { HasValue: true, Value: string s })
            {
                names.Add(s);
            }
            else
            {
                names.Add("?");
            }
        }

        return names.Count > 0 ? names : ["?"];
    }

    private static List<string> ExtractLambdaProperties(IAnonymousFunctionOperation lambda)
    {
        IOperation? returned = null;
        if (lambda.Body.Operations.OfType<IReturnOperation>().FirstOrDefault() is { ReturnedValue: not null } ret)
            returned = ret.ReturnedValue;

        if (returned == null)
            return ["?"];

        returned = UnwrapConversionAndDelegate(returned);
        if (returned is IMemberReferenceOperation memberRef)
            return [memberRef.Member.Name];

        if (returned is IAnonymousObjectCreationOperation anonObj)
        {
            var props = new List<string>();
            foreach (var init in anonObj.Initializers)
            {
                if (init is ISimpleAssignmentOperation assign &&
                    UnwrapConversionAndDelegate(assign.Value) is IMemberReferenceOperation propRef)
                {
                    props.Add(propRef.Member.Name);
                }
                else
                {
                    props.Add("?");
                }
            }

            return props.Count > 0 ? props : ["?"];
        }

        return ["?"];
    }

    private static IOperation UnwrapConversionAndDelegate(IOperation op)
    {
        while (true)
        {
            if (op is IConversionOperation conversion)
                op = conversion.Operand;
            else if (op is IDelegateCreationOperation delegateCreation)
                op = delegateCreation.Target;
            else
                return op;
        }
    }

    private static void AddMapsToEdge(
        string sourceId,
        string targetId,
        EfEmitContext ctx,
        (string? path, int? sl, int? sc, int? el, int? ec) location)
    {
        ctx.Edges.Add(ctx.Adapter.MakeEdge(sourceId, targetId, nameof(EdgeKind.MapsTo), Provenance.FrameworkDerived,
            ctx.ExtractorVersion, location));
    }

    private static void AddConstraintAnnotation(
        string symbolId,
        string kind,
        string value,
        string? documentPath,
        EfEmitContext ctx)
    {
        ctx.Annotations.Add(new AnnotationRecord(symbolId, kind, value, documentPath));
    }
}
