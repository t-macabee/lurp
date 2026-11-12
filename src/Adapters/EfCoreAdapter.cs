using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class EfCoreAdapter : IFrameworkAdapter
{
    public string Name => "EF Core";
    public string Version => "efcore-v1";
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

    /// <summary>The adapter context of one run with the lists that the run fills.</summary>
    private sealed record EfEmitContext(
        AdapterExtractionContext Adapter,
        string ExtractorVersion,
        List<EdgeRecord> Edges,
        List<AnnotationRecord> Annotations);

    private static void ExtractDbContextMappings(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (!IsDbContext(type))
                continue;

            // The walk is anchored to the DbContext type: the emitted edges and
            // annotations all carry evidence in its declaring document, which is
            // what the delete scope will have removed.
            if (!ctx.Adapter.IsSymbolInScope(type))
                continue;

            var dbContextId = ctx.Adapter.MakeSymbolId(type);
            if (dbContextId == null)
                continue;

            ExtractDbSetProperties(type, dbContextId, ctx);
            ExtractOnModelCreatingCalls(type, dbContextId, ctx);
        }
    }

    private static void ExtractDbSetProperties(INamedTypeSymbol type, string dbContextId, EfEmitContext ctx)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop || !IsDbSetType(prop.Type, out var entityType) || entityType == null)
                continue;

            var entityTypeId = ctx.Adapter.MakeSymbolId(entityType);
            if (entityTypeId == null)
                continue;

            var propId = ctx.Adapter.MakeSymbolId(prop);
            var sourceId = propId ?? dbContextId;

            AddMapsToEdge(sourceId, entityTypeId, ctx, ctx.Adapter.LocationResolver.Resolve(prop));
        }
    }

    private static void ExtractOnModelCreatingCalls(INamedTypeSymbol type, string dbContextId, EfEmitContext ctx)
    {
        var onModelCreating = type.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "OnModelCreating");

        if (onModelCreating == null)
            return;

        foreach (var syntaxRef in onModelCreating.DeclaringSyntaxReferences)
            if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
            {
                var semanticModel = ctx.Adapter.GetSemanticModel(methodSyntax.SyntaxTree);
                ExtractEntityCalls(methodSyntax, semanticModel, dbContextId, ctx);
                var (evidencePath, _, _, _, _) = ctx.Adapter.LocationResolver.Resolve(type);
                ExtractMethodConstraints(methodSyntax, semanticModel, dbContextId, evidencePath, ctx);
            }
    }

    private static void ExtractEntityTypeConfigurations(List<INamedTypeSymbol> allTypes, EfEmitContext ctx)
    {
        foreach (var type in allTypes)
            foreach (var iface in type.AllInterfaces)
            {
                if (iface.OriginalDefinition?.Name != "IEntityTypeConfiguration")
                    continue;

                // Same anchoring as the DbContext walk: the configuration
                // class's declaring document is the evidence document.
                if (!ctx.Adapter.IsSymbolInScope(type))
                    continue;

                var configId = ctx.Adapter.MakeSymbolId(type);
                if (configId == null)
                    continue;

                var entityTypeArg = iface.TypeArguments.FirstOrDefault();
                if (entityTypeArg is not INamedTypeSymbol entityType)
                    continue;

                var entityTypeId = ctx.Adapter.MakeSymbolId(entityType);
                if (entityTypeId == null)
                    continue;

                var typeLocation = ctx.Adapter.LocationResolver.Resolve(type);
                AddMapsToEdge(configId, entityTypeId, ctx, typeLocation);

                var evidencePath = typeLocation.path;

                var configureMethod = type.GetMembers()
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Name == "Configure");
                if (configureMethod != null)
                    foreach (var syntaxRef in configureMethod.DeclaringSyntaxReferences)
                        if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
                            ExtractEntityTypeConfigConstraints(methodSyntax, entityType, entityTypeId, evidencePath, ctx);
            }
    }

    private static void AddMapsToEdge(string sourceId, string targetId, EfEmitContext ctx, (string? path, int? sl, int? sc, int? el, int? ec) location)
    {
        ctx.Edges.Add(ctx.Adapter.MakeEdge(sourceId, targetId, nameof(EdgeKind.MapsTo), Provenance.FrameworkDerived, ctx.ExtractorVersion, location));
    }

    private static bool IsDbContext(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
            return false;

        var current = type.BaseType;
        while (current != null)
        {
            if (current.Name == "DbContext")
                return true;
            current = current.BaseType;
        }

        return false;
    }

    private static bool IsDbSetType(ITypeSymbol type, out INamedTypeSymbol? entityType)
    {
        entityType = null;

        if (type is not INamedTypeSymbol namedType)
            return false;

        var originalDef = namedType.OriginalDefinition;
        if (originalDef == null)
            return false;

        if (originalDef.Name != "DbSet")
            return false;

        if (namedType.TypeArguments is [INamedTypeSymbol entity])
        {
            entityType = entity;
            return true;
        }

        return false;
    }

    private static void ExtractEntityCalls(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string dbContextId, EfEmitContext ctx)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name is GenericNameSyntax { Identifier.Text: "Entity" } genericName) ExtractEntityMethodMapping(genericName, semanticModel, dbContextId, ctx);

            if (memberAccess.Name.Identifier.Text is "HasOne" or "HasMany" or "WithOne" or "WithMany") ExtractNavigationTypeReference(invocation, semanticModel, dbContextId, ctx);
        }
    }

    private static void ExtractEntityMethodMapping(GenericNameSyntax genericName, SemanticModel semanticModel, string dbContextId, EfEmitContext ctx)
    {
        if (genericName.TypeArgumentList.Arguments.Count != 1)
            return;

        var typeInfo = semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]);
        if (typeInfo.Type is not INamedTypeSymbol entityType)
            return;

        var entityTypeId = ctx.Adapter.MakeSymbolId(entityType);
        if (entityTypeId == null)
            return;

        AddMapsToEdge(dbContextId, entityTypeId, ctx, ctx.Adapter.LocationResolver.Resolve(genericName.GetLocation()));
    }

    private static void ExtractNavigationTypeReference(InvocationExpressionSyntax invocation, SemanticModel semanticModel, string dbContextId, EfEmitContext ctx)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol navMethod || navMethod.TypeArguments.Length == 0)
            return;

        if (navMethod.TypeArguments[0] is not INamedTypeSymbol navNamedType)
            return;

        var navTypeId = ctx.Adapter.MakeSymbolId(navNamedType);
        if (navTypeId == null)
            return;

        ctx.Edges.Add(ctx.Adapter.MakeEdge(dbContextId, navTypeId, nameof(EdgeKind.References), Provenance.FrameworkDerived, ctx.ExtractorVersion,
            ctx.Adapter.LocationResolver.Resolve(invocation.GetLocation())));
    }

    private static void ExtractMethodConstraints(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string dbContextId, string? evidencePath, EfEmitContext ctx)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            switch (memberAccess.Name.Identifier.Text)
            {
                case "HasQueryFilter":
                {
                    string? entityTypeId = null;
                    var entityName = "Unknown";

                    if (invocation.ArgumentList.Arguments.Count > 0)
                    {
                        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                        if (symbolInfo.Symbol is IMethodSymbol
                            {
                                ContainingType: INamedTypeSymbol { TypeArguments.Length: 1 } containingType
                            })
                        {
                            entityTypeId = containingType.TypeArguments[0] is INamedTypeSymbol entityType
                                ? ctx.Adapter.MakeSymbolId(entityType)
                                : null;
                            entityName = containingType.TypeArguments[0].Name;
                        }
                    }

                    var lambdaText = invocation.ArgumentList.Arguments.Count > 0
                        ? invocation.ArgumentList.Arguments[0].Expression.ToString()
                        : "";

                    AddConstraintAnnotation(
                        entityTypeId ?? dbContextId,
                        "ef_query_filter_constraint",
                        $"{entityName}: HasQueryFilter: {lambdaText}",
                        evidencePath,
                        ctx);
                    break;
                }
                case "HasDatabaseName" when invocation.ArgumentList.Arguments.Count > 0:
                {
                    var nameArg = invocation.ArgumentList.Arguments[0].Expression.ToString();
                    AddConstraintAnnotation(
                        dbContextId,
                        "ef_unique_index_constraint",
                        nameArg.Trim('"'),
                        evidencePath,
                        ctx);
                    break;
                }
            }
        }
    }

    private static void ExtractEntityTypeConfigConstraints(MethodDeclarationSyntax methodSyntax, INamedTypeSymbol entityType, string entityTypeId, string? evidencePath, EfEmitContext ctx)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            switch (memberAccess.Name.Identifier.Text)
            {
                case "HasQueryFilter" when invocation.ArgumentList.Arguments.Count > 0:
                {
                    var lambdaText = invocation.ArgumentList.Arguments[0].Expression.ToString();
                    AddConstraintAnnotation(
                        entityTypeId,
                        "ef_query_filter_constraint",
                        $"{entityType.Name}: HasQueryFilter: {lambdaText}",
                        evidencePath,
                        ctx);
                    break;
                }
                case "HasDatabaseName" when invocation.ArgumentList.Arguments.Count > 0:
                {
                    var nameArg = invocation.ArgumentList.Arguments[0].Expression.ToString();
                    AddConstraintAnnotation(
                        entityTypeId,
                        "ef_unique_index_constraint",
                        nameArg.Trim('"'),
                        evidencePath,
                        ctx);
                    break;
                }
            }
        }
    }

    private static void AddConstraintAnnotation(string symbolId, string kind, string value, string? documentPath, EfEmitContext ctx)
    {
        ctx.Annotations.Add(new AnnotationRecord(symbolId, kind, value, documentPath));
    }
}