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
    ///     them, and <c>IIndexStore.DeleteAnnotationsByDocumentPaths</c> retires
    ///     copied-forward rows over exactly the extraction scope, so extraction and
    ///     deletion narrow in lockstep.
    /// </remarks>
    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var compilation = context.Compilation;
        var snapshotId = context.SnapshotId;
        var locationResolver = context.LocationResolver;
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = AdapterTypeUtils.GetAllNamedTypes(compilation.Assembly.GlobalNamespace);
        var annotations = new List<AnnotationRecord>();
        var ctx = new ExtractionContext(assemblyIdentity, snapshotId, Version, edges, seen, locationResolver, Annotations: annotations);

        ExtractDbContextMappings(context, allTypes, ctx);
        ExtractEntityTypeConfigurations(allTypes, ctx, context);

        return new AdapterExtractionResult(edges, annotations);
    }

    private static void ExtractDbContextMappings(AdapterExtractionContext context, List<INamedTypeSymbol> allTypes, ExtractionContext ctx)
    {
        foreach (var type in allTypes)
        {
            if (!IsDbContext(type))
                continue;

            // The walk is anchored to the DbContext type: the emitted edges and
            // annotations all carry evidence in its declaring document, which is
            // what the delete scope will have removed.
            if (!context.IsSymbolInScope(type))
                continue;

            var dbContextId = SymbolIdFactory.Make(type, ctx.AssemblyIdentity);
            if (dbContextId == null)
                continue;

            ExtractDbSetProperties(type, dbContextId, ctx);
            ExtractOnModelCreatingCalls(context, type, dbContextId, ctx);
        }
    }

    private static void ExtractDbSetProperties(INamedTypeSymbol type, string dbContextId, ExtractionContext ctx)
    {
        foreach (var member in type.GetMembers())
        {
            if (member is not IPropertySymbol prop || !IsDbSetType(prop.Type, out var entityType) || entityType == null)
                continue;

            var entityTypeId = SymbolIdFactory.Make(entityType, ctx.AssemblyIdentity);
            if (entityTypeId == null)
                continue;

            var propId = SymbolIdFactory.Make(prop, ctx.AssemblyIdentity);
            var sourceId = propId ?? dbContextId;

            AddMapsToEdge(sourceId, entityTypeId, ctx, prop);
        }
    }

    private static void ExtractOnModelCreatingCalls(AdapterExtractionContext context, INamedTypeSymbol type, string dbContextId, ExtractionContext ctx)
    {
        var onModelCreating = type.GetMembers()
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Name == "OnModelCreating");

        if (onModelCreating == null)
            return;

        foreach (var syntaxRef in onModelCreating.DeclaringSyntaxReferences)
            if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
            {
                var semanticModel = context.GetSemanticModel(methodSyntax.SyntaxTree);
                ExtractEntityCalls(methodSyntax, semanticModel, dbContextId, ctx);
                var (evidencePath, _, _, _, _) = context.LocationResolver.Resolve(type);
                ExtractMethodConstraints(methodSyntax, semanticModel, dbContextId, evidencePath, ctx);
            }
    }

    private static void ExtractEntityTypeConfigurations(List<INamedTypeSymbol> allTypes, ExtractionContext ctx, AdapterExtractionContext context)
    {
        foreach (var type in allTypes)
            foreach (var iface in type.AllInterfaces)
            {
                if (iface.OriginalDefinition?.Name != "IEntityTypeConfiguration")
                    continue;

                // Same anchoring as the DbContext walk: the configuration
                // class's declaring document is the evidence document.
                if (!context.IsSymbolInScope(type))
                    continue;

                var configId = SymbolIdFactory.Make(type, ctx.AssemblyIdentity);
                if (configId == null)
                    continue;

                var entityTypeArg = iface.TypeArguments.FirstOrDefault();
                if (entityTypeArg is not INamedTypeSymbol entityType)
                    continue;

                var entityTypeId = SymbolIdFactory.Make(entityType, ctx.AssemblyIdentity);
                if (entityTypeId == null)
                    continue;

                AddMapsToEdge(configId, entityTypeId, ctx, type);

                var (evidencePath, _, _, _, _) = ctx.LocationResolver.Resolve(type);

                var configureMethod = type.GetMembers()
                    .OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Name == "Configure");
                if (configureMethod != null)
                    foreach (var syntaxRef in configureMethod.DeclaringSyntaxReferences)
                        if (syntaxRef.GetSyntax() is MethodDeclarationSyntax methodSyntax)
                            ExtractEntityTypeConfigConstraints(methodSyntax, entityType, entityTypeId, evidencePath, ctx);
            }
    }

    private static void AddMapsToEdge(string sourceId, string targetId, ExtractionContext ctx, ISymbol evidenceSymbol)
    {
        var key = (sourceId, targetId, EdgeKind.MapsTo.ToString());
        if (ctx.Seen.Add(key))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(evidenceSymbol);

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.MapsTo.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = "efcore-v1",
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = ctx.LocationResolver.IsGenerated(path)
            });
        }
    }

    private static void AddMapsToEdgeFromLocation(string sourceId, string targetId, ExtractionContext ctx, Location location)
    {
        var key = (sourceId, targetId, EdgeKind.MapsTo.ToString());
        if (ctx.Seen.Add(key))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(location);

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = sourceId,
                TargetSymbolId = targetId,
                Kind = EdgeKind.MapsTo.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = "efcore-v1",
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = ctx.LocationResolver.IsGenerated(path)
            });
        }
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

        if (namedType.TypeArguments.Length == 1 && namedType.TypeArguments[0] is INamedTypeSymbol entity)
        {
            entityType = entity;
            return true;
        }

        return false;
    }

    private static void ExtractEntityCalls(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string dbContextId, ExtractionContext ctx)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name is GenericNameSyntax genericName && genericName.Identifier.Text == "Entity") ExtractEntityMethodMapping(genericName, semanticModel, dbContextId, ctx);

            if (memberAccess.Name.Identifier.Text is "HasOne" or "HasMany" or "WithOne" or "WithMany") ExtractNavigationTypeReference(invocation, semanticModel, dbContextId, ctx);
        }
    }

    private static void ExtractEntityMethodMapping(GenericNameSyntax genericName, SemanticModel semanticModel, string dbContextId, ExtractionContext ctx)
    {
        if (genericName.TypeArgumentList.Arguments.Count != 1)
            return;

        var typeInfo = semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]);
        if (typeInfo.Type is not INamedTypeSymbol entityType)
            return;

        var entityTypeId = SymbolIdFactory.Make(entityType, ctx.AssemblyIdentity);
        if (entityTypeId == null)
            return;

        AddMapsToEdgeFromLocation(dbContextId, entityTypeId, ctx, genericName.GetLocation());
    }

    private static void ExtractNavigationTypeReference(InvocationExpressionSyntax invocation, SemanticModel semanticModel, string dbContextId, ExtractionContext ctx)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        if (symbolInfo.Symbol is not IMethodSymbol navMethod || navMethod.TypeArguments.Length == 0)
            return;

        if (navMethod.TypeArguments[0] is not INamedTypeSymbol navNamedType)
            return;

        var navTypeId = SymbolIdFactory.Make(navNamedType, ctx.AssemblyIdentity);
        if (navTypeId == null)
            return;

        var key = (dbContextId, navTypeId, EdgeKind.References.ToString());
        if (ctx.Seen.Add(key))
        {
            var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(invocation.GetLocation());

            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = dbContextId,
                TargetSymbolId = navTypeId,
                Kind = EdgeKind.References.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = "efcore-v1",
                SourceDocumentPath = path,
                SourceStartLine = sl,
                SourceStartColumn = sc,
                SourceEndLine = el,
                SourceEndColumn = ec,
                IsCrossGenerated = ctx.LocationResolver.IsGenerated(path)
            });
        }
    }

    private static void ExtractMethodConstraints(MethodDeclarationSyntax methodSyntax, SemanticModel semanticModel, string dbContextId, string? evidencePath, ExtractionContext ctx)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name.Identifier.Text == "HasQueryFilter")
            {
                string? entityTypeId = null;
                var entityName = "Unknown";

                if (invocation.ArgumentList.Arguments.Count > 0)
                {
                    var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                    if (symbolInfo.Symbol is IMethodSymbol methodSymbol
                        && methodSymbol.ContainingType is INamedTypeSymbol containingType
                        && containingType.TypeArguments.Length == 1)
                    {
                        entityTypeId = containingType.TypeArguments[0] is INamedTypeSymbol entityType
                            ? SymbolIdFactory.Make(entityType, ctx.AssemblyIdentity)
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
            }

            if (memberAccess.Name.Identifier.Text == "HasDatabaseName"
                && invocation.ArgumentList.Arguments.Count > 0)
            {
                var nameArg = invocation.ArgumentList.Arguments[0].Expression.ToString();
                AddConstraintAnnotation(
                    dbContextId,
                    "ef_unique_index_constraint",
                    nameArg.Trim('"'),
                    evidencePath,
                    ctx);
            }
        }
    }

    private static void ExtractEntityTypeConfigConstraints(MethodDeclarationSyntax methodSyntax, INamedTypeSymbol entityType, string entityTypeId, string? evidencePath, ExtractionContext ctx)
    {
        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            if (memberAccess.Name.Identifier.Text == "HasQueryFilter"
                && invocation.ArgumentList.Arguments.Count > 0)
            {
                var lambdaText = invocation.ArgumentList.Arguments[0].Expression.ToString();
                AddConstraintAnnotation(
                    entityTypeId,
                    "ef_query_filter_constraint",
                    $"{entityType.Name}: HasQueryFilter: {lambdaText}",
                    evidencePath,
                    ctx);
            }

            if (memberAccess.Name.Identifier.Text == "HasDatabaseName"
                && invocation.ArgumentList.Arguments.Count > 0)
            {
                var nameArg = invocation.ArgumentList.Arguments[0].Expression.ToString();
                AddConstraintAnnotation(
                    entityTypeId,
                    "ef_unique_index_constraint",
                    nameArg.Trim('"'),
                    evidencePath,
                    ctx);
            }
        }
    }

    private static void AddConstraintAnnotation(string symbolId, string kind, string value, string? documentPath, ExtractionContext ctx)
    {
        ctx.Annotations?.Add(new AnnotationRecord(symbolId, kind, value, documentPath));
    }
}