using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class AspNetCoreAdapter : IFrameworkAdapter
{
    public string Name => "ASP.NET Core";
    public string Version => "aspnetcore-v1";
    public string Description => "ASP.NET Core framework edges (controller actions, middleware)";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var compilation = context.Compilation;
        var snapshotId = context.SnapshotId;
        var locationResolver = context.LocationResolver;
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = AdapterTypeUtils.GetAllNamedTypes(compilation.Assembly.GlobalNamespace);
        var ctx = new ExtractionContext(assemblyIdentity, snapshotId, Version, edges, seen, locationResolver);

        foreach (var type in allTypes)
        {
            if (!IsController(type))
                continue;

            if (!context.IsSymbolInScope(type))
                continue;

            var controllerId = SymbolIdFactory.Make(type, assemblyIdentity);
            if (controllerId == null)
                continue;

            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                    continue;

                ProcessControllerAction(method, controllerId, ctx);
            }
        }

        return edges;
    }

    private void ProcessControllerAction(IMethodSymbol method, string controllerId, ExtractionContext ctx)
    {
        var methodId = SymbolIdFactory.Make(method, ctx.AssemblyIdentity);
        if (methodId == null)
            return;

        // All edges from this action are anchored to the action method declaration
        var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(method);
        var loc = new EdgeLocation(path, sl, sc, el, ec, ctx.LocationResolver.IsGenerated(path));

        var declaresKey = (controllerId, methodId, nameof(EdgeKind.Declares));
        if (ctx.Seen.Add(declaresKey))
            ctx.Edges.Add(MakeEdge(controllerId, methodId, nameof(EdgeKind.Declares), ctx.SnapshotId, loc));

        EmitRouteEdge(method, methodId, ctx, loc);
        EmitReturnTypeEdge(method, methodId, ctx, loc);
        EmitFromServicesEdges(method, methodId, ctx, loc);
    }

    private void EmitRouteEdge(IMethodSymbol method, string methodId, ExtractionContext ctx, EdgeLocation loc)
    {
        var routeTemplate = ExtractRouteTemplate((INamedTypeSymbol)method.ContainingType, method);
        if (routeTemplate == null)
            return;

        var routeSourceId = $"{GraphNodeIds.RoutePrefix}{routeTemplate}";
        var routeKey = (routeSourceId, methodId, nameof(EdgeKind.RoutesTo));
        if (ctx.Seen.Add(routeKey))
            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = routeSourceId,
                TargetSymbolId = methodId,
                Kind = nameof(EdgeKind.RoutesTo),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = Version,
                SourceDocumentPath = loc.Path,
                SourceStartLine = loc.StartLine,
                SourceStartColumn = loc.StartColumn,
                SourceEndLine = loc.EndLine,
                SourceEndColumn = loc.EndColumn,
                IsCrossGenerated = loc.IsGenerated,
                SourceNodeKind = GraphNodeKind.Route
            });
    }

    private static void EmitReturnTypeEdge(IMethodSymbol method, string methodId, ExtractionContext ctx, EdgeLocation loc)
    {
        if (method.ReturnsVoid || method.ReturnType == null)
            return;

        var returnTypeId = SymbolIdFactory.Make(method.ReturnType, ctx.AssemblyIdentity);
        if (returnTypeId == null)
            return;

        var retKey = (methodId, returnTypeId, nameof(EdgeKind.Returns));
        if (ctx.Seen.Add(retKey))
            ctx.Edges.Add(MakeEdge(methodId, returnTypeId, nameof(EdgeKind.Returns), ctx.SnapshotId, loc));
    }

    private static void EmitFromServicesEdges(IMethodSymbol method, string methodId, ExtractionContext ctx, EdgeLocation loc)
    {
        foreach (var param in method.Parameters)
        {
            var hasFromServices = param.GetAttributes().Any(a => a.AttributeClass?.Name is "FromServicesAttribute" or "FromServices");
            if (!hasFromServices)
                continue;

            var paramTypeId = SymbolIdFactory.Make(param.Type, ctx.AssemblyIdentity);
            if (paramTypeId == null)
                continue;

            var refKey = (methodId, paramTypeId, nameof(EdgeKind.References));
            if (ctx.Seen.Add(refKey))
                ctx.Edges.Add(MakeEdge(methodId, paramTypeId, nameof(EdgeKind.References), ctx.SnapshotId, loc));
        }
    }

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class)
            return false;

        var current = type.BaseType;

        while (current != null)
        {
            if (current.Name is "ControllerBase" or "Controller")
                return true;

            current = current.BaseType;
        }

        return false;
    }

    private static string? ExtractRouteTemplate(INamedTypeSymbol controller, IMethodSymbol action)
    {
        var parts = new List<string>();

        var classRoute = controller.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "RouteAttribute" or "Route");

        if (classRoute?.ConstructorArguments.Length > 0 && classRoute.ConstructorArguments[0].Value is string classTemplate) parts.Add(classTemplate.TrimStart('/'));

        var methodRoute = action.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.Name is "RouteAttribute" or "Route" or "HttpGetAttribute" or "HttpGet" or "HttpPostAttribute" or "HttpPost" or "HttpPutAttribute" or "HttpPut" or "HttpDeleteAttribute" or "HttpDelete" or "HttpPatchAttribute"
                or "HttpPatch");

        if (methodRoute?.ConstructorArguments.Length > 0 && methodRoute.ConstructorArguments[0].Value is string methodTemplate)
        {
            if (methodTemplate.StartsWith('/'))
                return methodTemplate.TrimStart('/');

            parts.Add(methodTemplate);
        }

        return parts.Count > 0 ? string.Join("/", parts) : null;
    }

    private static EdgeRecord MakeEdge(string sourceId, string targetId, string kind, string snapshotId, EdgeLocation loc)
    {
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = Provenance.FrameworkDerived,
            SnapshotId = snapshotId,
            ExtractorVersion = "aspnetcore-v1",
            SourceDocumentPath = loc.Path,
            SourceStartLine = loc.StartLine,
            SourceStartColumn = loc.StartColumn,
            SourceEndLine = loc.EndLine,
            SourceEndColumn = loc.EndColumn,
            IsCrossGenerated = loc.IsGenerated
        };
    }

    private readonly record struct EdgeLocation(string? Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn, bool IsGenerated);
}