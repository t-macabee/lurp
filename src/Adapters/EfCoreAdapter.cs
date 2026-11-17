using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class EfCoreAdapter : IFrameworkAdapter
{
    private const string DbContextMetadataName = "Microsoft.EntityFrameworkCore.DbContext";
    private const string DbSetMetadataName = "Microsoft.EntityFrameworkCore.DbSet`1";
    private const string EntityTypeConfigMetadataName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";
    private const string ModelBuilderMetadataName = "Microsoft.EntityFrameworkCore.ModelBuilder";
    private const string EntityTypeBuilderMetadataName = "Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder`1";
    private const string IndexBuilderMetadataName = "Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder";
    private const string IndexBuilderGenericMetadataName = "Microsoft.EntityFrameworkCore.Metadata.Builders.IndexBuilder`1";

    public string Name => "EF Core";
    public string Version => "efcore-v2";
    public string Description => "Entity Framework Core edges (DbSets, entity mappings)";

    /// <remarks>
    ///     Honors <see cref="AdapterExtractionContext.ScopeDocuments" /> like the other
    ///     five adapters: each walk is guarded by the declaring scope of the type it is
    ///     anchored to (the <c>DbContext</c> or <c>IEntityTypeConfiguration&lt;T&gt;</c>
    ///     class). Annotations carry the evidence document of the walk that produced
    ///     them, and <c>IEdgeStore.DeleteAnnotationsByDocumentPaths</c> retires
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

    private static void ExtractDbContextMappings(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (type.TypeKind != TypeKind.Class || !FrameworkTypes.DerivesFrom(type, DbContextMetadataName))
                continue;

            if (!ctx.Adapter.IsSymbolInScope(type))
                continue;

            var dbContextId = ctx.Adapter.MakeSymbolId(type);
            if (dbContextId == null)
                continue;

            ExtractDbSetProperties(type, dbContextId, ctx);

            var anchorLocation = ctx.Adapter.LocationResolver.Resolve(type);
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (!OverridesOnModelCreating(method))
                    continue;

                WalkRootMethod(method, type, dbContextId, anchorLocation, configEntityType: null, ctx);
            }
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

    private static void ExtractDbSetProperties(INamedTypeSymbol type, string dbContextId, EfEmitContext ctx)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop ||
                prop.Type is not INamedTypeSymbol propType ||
                !FrameworkTypes.Is(propType, DbSetMetadataName) ||
                propType.TypeArguments is not [INamedTypeSymbol entityType])
                continue;

            var entityTypeId = ctx.Adapter.MakeSymbolId(entityType);
            if (entityTypeId == null)
                continue;

            var propId = ctx.Adapter.MakeSymbolId(prop);
            var sourceId = propId ?? dbContextId;

            AddMapsToEdge(sourceId, entityTypeId, ctx, ctx.Adapter.LocationResolver.Resolve(prop));
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

                var entityTypeId = ctx.Adapter.MakeSymbolId(entityType);
                if (entityTypeId == null)
                    continue;

                AddMapsToEdge(configId, entityTypeId, ctx, anchorLocation);

                var ifaceConfigure = iface.GetMembers("Configure").OfType<IMethodSymbol>().FirstOrDefault();
                if (ifaceConfigure == null)
                    continue;

                if (type.FindImplementationForInterfaceMember(ifaceConfigure) is IMethodSymbol configureImpl)
                    WalkRootMethod(configureImpl, type, configId, anchorLocation, entityType, ctx);
            }
        }
    }

    private static void WalkRootMethod(
        IMethodSymbol method,
        INamedTypeSymbol anchorType,
        string anchorId,
        (string? path, int? sl, int? sc, int? el, int? ec) anchorLocation,
        INamedTypeSymbol? configEntityType,
        EfEmitContext ctx)
    {
        var declaredInAnchor = SymbolEqualityComparer.Default.Equals(method.ContainingType, anchorType);
        var evidencePath = anchorLocation.path;

        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            var semanticModel = ctx.Adapter.GetSemanticModel(syntax.SyntaxTree);
            var rootOp = semanticModel.GetOperation(syntax);
            if (rootOp == null)
                continue;

            foreach (var operation in rootOp.DescendantsAndSelf())
            {
                if (operation is not IInvocationOperation invocation)
                    continue;

                var target = invocation.TargetMethod;
                var location = declaredInAnchor
                    ? ctx.Adapter.LocationResolver.Resolve(invocation.Syntax.GetLocation())
                    : anchorLocation;

                if (target.Name == "Entity" &&
                    FrameworkTypes.Is(target.ContainingType, ModelBuilderMetadataName) &&
                    target.TypeArguments is [var rawEntity])
                {
                    var entity = ResolveEntityType(rawEntity, configEntityType);
                    var entityId = entity != null ? ctx.Adapter.MakeSymbolId(entity) : null;
                    if (entityId != null)
                        AddMapsToEdge(anchorId, entityId, ctx, location);
                    continue;
                }

                if (!FrameworkTypes.Is(target.ContainingType, EntityTypeBuilderMetadataName))
                    continue;

                if (target.Name is "HasOne" or "HasMany" && target.TypeArguments is [var rawRel])
                {
                    var relEntity = ResolveEntityType(rawRel, configEntityType);
                    var relId = relEntity != null ? ctx.Adapter.MakeSymbolId(relEntity) : null;
                    if (relId != null)
                    {
                        ctx.Edges.Add(ctx.Adapter.MakeEdge(anchorId, relId, nameof(EdgeKind.References),
                            Provenance.FrameworkDerived, ctx.ExtractorVersion, location));
                    }

                    continue;
                }

                if (target.ContainingType.TypeArguments is not [var rawBuilderEntity])
                    continue;

                var builderEntity = ResolveEntityType(rawBuilderEntity, configEntityType);
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

    private static INamedTypeSymbol? ResolveEntityType(ITypeSymbol candidate, INamedTypeSymbol? configEntityType)
    {
        if (candidate is ITypeParameterSymbol && configEntityType != null)
            return configEntityType;

        return candidate as INamedTypeSymbol;
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
