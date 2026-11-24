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
        List<AnnotationRecord> Annotations);

    private sealed class ModelMappings
    {
        internal List<(INamedTypeSymbol Type, string Source, (string? path, int? sl, int? sc, int? el, int? ec) Location)> Explicit { get; } = [];
        internal HashSet<INamedTypeSymbol> IgnoredTypes { get; } = new(SymbolEqualityComparer.Default);
        internal HashSet<(INamedTypeSymbol Type, string Name)> IgnoredProperties { get; } = new(TypeMemberComparer.Instance);
    }

    private sealed class TypeMemberComparer : IEqualityComparer<(INamedTypeSymbol Type, string Name)>
    {
        internal static readonly TypeMemberComparer Instance = new();

        public bool Equals((INamedTypeSymbol Type, string Name) x, (INamedTypeSymbol Type, string Name) y)
            => SymbolEqualityComparer.Default.Equals(x.Type, y.Type) && StringComparer.Ordinal.Equals(x.Name, y.Name);

        public int GetHashCode((INamedTypeSymbol Type, string Name) item)
            => HashCode.Combine(SymbolEqualityComparer.Default.GetHashCode(item.Type), StringComparer.Ordinal.GetHashCode(item.Name));
    }

    private static void ExtractDbContextMappings(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || !FrameworkTypes.DerivesFrom(type, DbContextMetadataName))
                continue;

            if (!ctx.Adapter.IsSymbolInScope(type) || ctx.Adapter.MakeSymbolId(type) is not { } dbContextId)
                continue;

            var anchorLocation = ctx.Adapter.LocationResolver.Resolve(type);
            var mappings = new ModelMappings();
            ExtractDbSetProperties(type, anchorLocation, ctx, mappings);

            for (var current = type; current != null && !FrameworkTypes.Is(current, DbContextMetadataName); current = current.BaseType)
            {
                var method = current.GetMembers("OnModelCreating").OfType<IMethodSymbol>().FirstOrDefault(OverridesOnModelCreating);
                if (method == null)
                    continue;

                WalkRootMethod(method, type, dbContextId, anchorLocation, configEntityType: null, ctx, mappings);
                break;
            }

            EmitMappings(mappings, dbContextId, anchorLocation, ctx);
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

                if (ctx.Adapter.MakeSymbolId(prop) is not { } propId)
                    continue;

                var location = inherited ? anchorLocation : ctx.Adapter.LocationResolver.Resolve(prop);
                mappings.Explicit.Add((entityType, propId, location));
            }
        }
    }

    private static void ExtractEntityTypeConfigurations(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract)
                continue;

            if (!ctx.Adapter.IsSymbolInScope(type))
                continue;

            var configId = ctx.Adapter.MakeSymbolId(type);
            if (configId == null)
                continue;

            var anchorLocation = ctx.Adapter.LocationResolver.Resolve(type);

            foreach (var iface in type.AllInterfaces)
            {
                if (!FrameworkTypes.Is(iface, EntityTypeConfigMetadataName) ||
                    iface.TypeArguments is not [INamedTypeSymbol entityType])
                    continue;

                var mappings = new ModelMappings();
                mappings.Explicit.Add((entityType, configId, anchorLocation));

                var ifaceConfigure = iface.GetMembers("Configure").OfType<IMethodSymbol>().FirstOrDefault();
                if (ifaceConfigure != null && type.FindImplementationForInterfaceMember(ifaceConfigure) is IMethodSymbol configureImpl)
                    WalkRootMethod(configureImpl, type, configId, anchorLocation, entityType, ctx, mappings);

                EmitMappings(mappings, configId, anchorLocation, ctx);
            }
        }
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
        var declaredInAnchor = SymbolEqualityComparer.Default.Equals(method.ContainingType, anchorType);
        var evidencePath = anchorLocation.path;

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            var semanticModel = ctx.Adapter.GetDeclaringSemanticModel(syntax.SyntaxTree);
            var rootOp = semanticModel?.GetOperation(syntax);
            if (rootOp == null)
                continue;

            foreach (var operation in rootOp.DescendantsAndSelf())
            {
                if (operation is not IInvocationOperation invocation)
                    continue;

                var target = invocation.TargetMethod;
                if (method.OverriddenMethod is { } overridden && OverridesOnModelCreating(overridden) &&
                    invocation.Syntax is InvocationExpressionSyntax
                    { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } } &&
                    SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, overridden.OriginalDefinition))
                {
                    WalkRootMethod(overridden, anchorType, anchorId, anchorLocation, configEntityType, ctx, mappings);
                    continue;
                }

                var location = declaredInAnchor
                    ? ctx.Adapter.LocationResolver.Resolve(invocation.Syntax.GetLocation())
                    : anchorLocation;

                var rawType = target.TypeArguments.FirstOrDefault() ??
                    invocation.Arguments.Select(a => UnwrapConversionAndDelegate(a.Value))
                        .OfType<ITypeOfOperation>().FirstOrDefault()?.TypeOperand;
                var explicitType = ResolveEntityType(rawType, method, configEntityType);

                if (FrameworkTypes.Is(target.ContainingType, ModelBuilderMetadataName))
                {
                    if (target.Name == "Entity" && explicitType != null)
                        mappings.Explicit.Add((explicitType, anchorId, location));
                    if (target.Name == "Ignore" && explicitType != null)
                        mappings.IgnoredTypes.Add(explicitType);
                    continue;
                }

                var builder = target.ContainingType;
                var entityBuilder = IsBuilder(builder, "EntityTypeBuilder", 1);
                if (!entityBuilder && !IsBuilder(builder, "OwnedNavigationBuilder", 2) &&
                    !IsBuilder(builder, "ComplexPropertyBuilder", 1) && !IsBuilder(builder, "ComplexCollectionBuilder", 1))
                    continue;

                var builderEntity = ResolveEntityType(builder.TypeArguments.LastOrDefault(), method, configEntityType)
                    ?? GetBuilderEntity(invocation.Instance, method, configEntityType);

                if (target.Name is "OwnsOne" or "OwnsMany" or "ComplexProperty" or "ComplexCollection")
                {
                    var mapped = explicitType ?? NavigationType(FindConfiguredProperty(invocation, builderEntity)?.Type);
                    if (mapped != null)
                        mappings.Explicit.Add((mapped, anchorId, location));
                    continue;
                }

                if (target.Name == "Ignore" && builderEntity != null)
                {
                    var name = FindConfiguredProperty(invocation, builderEntity)?.Name ?? ReadMemberName(invocation);
                    if (name != null)
                        mappings.IgnoredProperties.Add((builderEntity, name));
                    continue;
                }

                if (!entityBuilder)
                    continue;

                if (target.Name is "HasOne" or "HasMany" && target.TypeArguments is [var rawRel])
                {
                    var relEntity = ResolveEntityType(rawRel, method, configEntityType);
                    var relId = relEntity != null ? ctx.Adapter.MakeSymbolId(relEntity) : null;
                    if (relId != null)
                    {
                        ctx.Edges.Add(ctx.Adapter.MakeEdge(anchorId, relId, nameof(EdgeKind.References),
                            Provenance.FrameworkDerived, ctx.ExtractorVersion, location));
                    }

                    continue;
                }

                if (builderEntity == null)
                    continue;

                var builderEntityId = ctx.Adapter.MakeSymbolId(builderEntity);
                if (builderEntityId == null)
                    continue;

                if (target.Name == "HasQueryFilter")
                {
                    EmitQueryFilter(invocation, builderEntity, builderEntityId, evidencePath, ctx);
                }
                else if (target.Name == "HasIndex")
                {
                    EmitUniqueIndexIfUnique(invocation, builderEntity, builderEntityId, evidencePath, ctx);
                }
            }
        }
    }

    private static bool IsBuilder(INamedTypeSymbol type, string name, int genericArity)
    {
        const string prefix = "Microsoft.EntityFrameworkCore.Metadata.Builders.";
        return FrameworkTypes.Is(type, prefix + name) ||
               FrameworkTypes.Is(type, $"{prefix}{name}`{genericArity}");
    }

    private static INamedTypeSymbol? ResolveEntityType(ITypeSymbol? candidate, IMethodSymbol method, INamedTypeSymbol? configEntityType)
    {
        if (candidate is ITypeParameterSymbol parameter)
        {
            if (parameter.TypeParameterKind == TypeParameterKind.Type &&
                SymbolEqualityComparer.Default.Equals(parameter.ContainingSymbol, method.ContainingType.OriginalDefinition))
            {
                candidate = method.ContainingType.TypeArguments[parameter.Ordinal];
            }

            if (candidate is ITypeParameterSymbol)
                return configEntityType;
        }

        return candidate as INamedTypeSymbol;
    }

    private static INamedTypeSymbol? GetBuilderEntity(IOperation? instance, IMethodSymbol method, INamedTypeSymbol? configEntityType)
    {
        if (instance is IParameterReferenceOperation parameter)
        {
            for (var current = instance.Parent; current != null; current = current.Parent)
            {
                if (current is not IAnonymousFunctionOperation lambda ||
                    !lambda.Symbol.Parameters.Any(candidate =>
                        SymbolEqualityComparer.Default.Equals(candidate, parameter.Parameter)))
                    continue;

                for (var outer = lambda.Parent; outer != null; outer = outer.Parent)
                {
                    if (outer is not IInvocationOperation enclosing)
                        continue;

                    if (enclosing.TargetMethod.Name != "Entity" ||
                        !FrameworkTypes.Is(enclosing.TargetMethod.ContainingType, ModelBuilderMetadataName))
                        return null;

                    return ResolveEntityType(enclosing.Arguments.Select(a => UnwrapConversionAndDelegate(a.Value))
                        .OfType<ITypeOfOperation>().FirstOrDefault()?.TypeOperand, method, configEntityType);
                }

                return null;
            }

            return null;
        }

        if (instance is not IInvocationOperation invocation)
            return null;

        if (invocation.TargetMethod.Name == "Entity" && FrameworkTypes.Is(invocation.TargetMethod.ContainingType, ModelBuilderMetadataName))
        {
            return ResolveEntityType(invocation.Arguments.Select(a => UnwrapConversionAndDelegate(a.Value))
                .OfType<ITypeOfOperation>().FirstOrDefault()?.TypeOperand, method, configEntityType);
        }

        return GetBuilderEntity(InvocationReceiver(invocation), method, configEntityType);
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
        EfEmitContext ctx)
    {
        var mappedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var expandedDefinitions = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var queue = new Queue<INamedTypeSymbol>();

        foreach (var (type, source, location) in mappings.Explicit)
        {
            if (mappings.IgnoredTypes.Contains(type) || !ctx.Adapter.IsSolutionSymbol(type) ||
                ctx.Adapter.MakeSymbolId(type) is not { } id)
                continue;

            AddMapsToEdge(source, id, ctx, location);
            if (mappedTypes.Add(type))
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
                        mappings.IgnoredTypes.Contains(target) || IsNotMapped(target) ||
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
        string entityTypeId,
        string? evidencePath,
        EfEmitContext ctx)
    {
        if (invocation.Arguments.Length == 1)
        {
            var filterText = invocation.Arguments[0].Value.Syntax.ToString();
            AddConstraintAnnotation(
                entityTypeId,
                "ef_query_filter_constraint",
                $"{entityType.Name}: HasQueryFilter: {filterText}",
                evidencePath,
                ctx);
        }
        else if (invocation.Arguments.Length == 2)
        {
            var keyArg = invocation.Arguments[0].Value;
            var keyText = keyArg.ConstantValue is { HasValue: true, Value: string s }
                ? s
                : keyArg.Syntax.ToString();
            var filterText = invocation.Arguments[1].Value.Syntax.ToString();
            AddConstraintAnnotation(
                entityTypeId,
                "ef_query_filter_constraint",
                $"{entityType.Name}: HasQueryFilter {keyText}: {filterText}",
                evidencePath,
                ctx);
        }
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
