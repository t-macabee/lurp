using Lurp.Workspace;
﻿using Microsoft.CodeAnalysis;
using Lurp.Storage;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class AspNetCoreAdapter : IFrameworkAdapter
{
    public string Name => "ASP.NET Core";
    public string Version => "aspnetcore-v1";

    public List<EdgeRecord> Extract(Compilation compilation, string snapshotId, EdgeLocationResolver locationResolver)
    {
        var edges = new List<EdgeRecord>();
        var seen = new HashSet<(string source, string target, string kind)>();
        var assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        var allTypes = GetAllNamedTypes(compilation.Assembly.GlobalNamespace);
        var ctx = new ExtractionContext(assemblyIdentity, snapshotId, edges, seen, locationResolver);

        foreach (var type in allTypes)
        {
            if (!IsController(type))
                continue;

            var controllerId = MakeSymbolId(type, assemblyIdentity);
            if (controllerId == null)
                continue;

            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol method || method.MethodKind != MethodKind.Ordinary)
                    continue;

                ProcessControllerAction(method, controllerId, ctx);
            }
        }

        return edges;
    }

    private readonly record struct EdgeLocation(string? Path, int? StartLine, int? StartColumn, int? EndLine, int? EndColumn, bool IsGenerated);

    private void ProcessControllerAction(IMethodSymbol method, string controllerId, ExtractionContext ctx)
    {
        var methodId = MakeSymbolId(method, ctx.AssemblyIdentity);
        if (methodId == null)
            return;

        // All edges from this action are anchored to the action method declaration
        var (path, sl, sc, el, ec) = ctx.LocationResolver.Resolve(method);
        var loc = new EdgeLocation(path, sl, sc, el, ec, ctx.LocationResolver.IsGenerated(path));

        var declaresKey = (controllerId, methodId, EdgeKind.Declares.ToString());
        if (ctx.Seen.Add(declaresKey))
            ctx.Edges.Add(MakeEdge(controllerId, methodId, EdgeKind.Declares.ToString(), ctx.SnapshotId, loc));

        EmitRouteEdge(method, methodId, ctx, loc);
        EmitReturnTypeEdge(method, methodId, ctx, loc);
        EmitFromServicesEdges(method, methodId, ctx, loc);
    }

    private void EmitRouteEdge(IMethodSymbol method, string methodId, ExtractionContext ctx, EdgeLocation loc)
    {
        var routeTemplate = ExtractRouteTemplate((INamedTypeSymbol)method.ContainingType, method);
        if (routeTemplate == null)
            return;

        var routeSourceId = $"route://{routeTemplate}";
        var routeKey = (routeSourceId, methodId, EdgeKind.RoutesTo.ToString());
        if (ctx.Seen.Add(routeKey))
            ctx.Edges.Add(new EdgeRecord
            {
                SourceSymbolId = routeSourceId,
                TargetSymbolId = methodId,
                Kind = EdgeKind.RoutesTo.ToString(),
                Provenance = Provenance.FrameworkDerived,
                SnapshotId = ctx.SnapshotId,
                ExtractorVersion = Version,
                SourceDocumentPath = loc.Path,
                SourceStartLine = loc.StartLine,
                SourceStartColumn = loc.StartColumn,
                SourceEndLine = loc.EndLine,
                SourceEndColumn = loc.EndColumn,
                IsCrossGenerated = loc.IsGenerated,
            });
    }

    private void EmitReturnTypeEdge(IMethodSymbol method, string methodId, ExtractionContext ctx, EdgeLocation loc)
    {
        if (method.ReturnsVoid || method.ReturnType == null)
            return;

        var returnTypeId = MakeSymbolId(method.ReturnType, ctx.AssemblyIdentity);
        if (returnTypeId == null)
            return;

        var retKey = (methodId, returnTypeId, EdgeKind.Returns.ToString());
        if (ctx.Seen.Add(retKey))
            ctx.Edges.Add(MakeEdge(methodId, returnTypeId, EdgeKind.Returns.ToString(), ctx.SnapshotId, loc));
    }

    private void EmitFromServicesEdges(IMethodSymbol method, string methodId, ExtractionContext ctx, EdgeLocation loc)
    {
        foreach (var param in method.Parameters)
        {
            var hasFromServices = param.GetAttributes().Any(a => a.AttributeClass?.Name is "FromServicesAttribute" or "FromServices");
            if (!hasFromServices)
                continue;

            var paramTypeId = MakeSymbolId(param.Type, ctx.AssemblyIdentity);
            if (paramTypeId == null)
                continue;

            var refKey = (methodId, paramTypeId, EdgeKind.References.ToString());
            if (ctx.Seen.Add(refKey))
                ctx.Edges.Add(MakeEdge(methodId, paramTypeId, EdgeKind.References.ToString(), ctx.SnapshotId, loc));
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

        if (classRoute?.ConstructorArguments.Length > 0 && classRoute.ConstructorArguments[0].Value is string classTemplate)
        {
            parts.Add(classTemplate.TrimStart('/'));
        }

        var methodRoute = action.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name is "RouteAttribute" or "Route" or "HttpGetAttribute" or "HttpGet" or "HttpPostAttribute" or "HttpPost" or "HttpPutAttribute" or "HttpPut" or "HttpDeleteAttribute" or "HttpDelete" or "HttpPatchAttribute" or "HttpPatch");

        if (methodRoute?.ConstructorArguments.Length > 0 && methodRoute.ConstructorArguments[0].Value is string methodTemplate)
        {
            if (methodTemplate.StartsWith("/"))
                return methodTemplate.TrimStart('/');

            parts.Add(methodTemplate);
        }

        return parts.Count > 0 ? string.Join("/", parts) : null;
    }

    private static string? MakeSymbolId(ISymbol symbol, string assemblyIdentity)
    {
        return SymbolIdFactory.Make(symbol, assemblyIdentity);
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
            IsCrossGenerated = loc.IsGenerated,
        };
    }

    private static List<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol ns)
    {
        var types = new List<INamedTypeSymbol>();

        CollectTypes(ns, types);

        return types;
    }

    private static void CollectTypes(INamespaceSymbol ns, List<INamedTypeSymbol> types)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            types.Add(type);

            foreach (var nested in type.GetTypeMembers())
                types.Add(nested);
        }

        foreach (var childNs in ns.GetNamespaceMembers())
            CollectTypes(childNs, types);
    }
}
