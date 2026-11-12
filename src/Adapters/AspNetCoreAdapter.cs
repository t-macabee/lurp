using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class AspNetCoreAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "aspnetcore-v2";

    public string Name => "ASP.NET Core";
    public string Version => AdapterVersion;
    public string Description => "ASP.NET Core controller action routes";

    public AdapterExtractionResult Extract(AdapterExtractionContext context)
    {
        var edges = new List<EdgeRecord>();

        foreach (var type in context.GetAllNamedTypes())
        {
            if (!IsController(type))
                continue;

            if (!context.IsSymbolInScope(type))
                continue;

            var controllerId = context.MakeSymbolId(type);
            if (controllerId == null)
                continue;

            foreach (var member in type.GetMembers())
            {
                if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method || !IsAction(type, method))
                    continue;

                ProcessControllerAction(method, controllerId, context, edges);
            }
        }

        return edges;
    }

    private static void ProcessControllerAction(IMethodSymbol method, string controllerId, AdapterExtractionContext context, List<EdgeRecord> edges)
    {
        var methodId = context.MakeSymbolId(method);
        if (methodId == null)
            return;

        // All edges from this action are anchored to the action method declaration
        var location = context.LocationResolver.Resolve(method);

        edges.Add(context.MakeEdge(controllerId, methodId, nameof(EdgeKind.Declares), Provenance.FrameworkDerived, AdapterVersion, location));

        EmitRouteEdge(method, methodId, context, edges, location);
        EmitReturnTypeEdge(method, methodId, context, edges, location);
        EmitFromServicesEdges(method, methodId, context, edges, location);
    }

    private static void EmitRouteEdge(IMethodSymbol method, string methodId, AdapterExtractionContext context, List<EdgeRecord> edges,
        (string? path, int? sl, int? sc, int? el, int? ec) location)
    {
        var routeTemplate = ExtractRouteTemplate((INamedTypeSymbol)method.ContainingType, method);
        if (routeTemplate == null)
            return;

        var routeSourceId = $"{GraphNodeIds.RoutePrefix}{routeTemplate}";
        edges.Add(context.MakeEdge(routeSourceId, methodId, nameof(EdgeKind.RoutesTo), Provenance.FrameworkDerived, AdapterVersion, location,
            sourceNodeKind: GraphNodeKind.Route));
    }

    private static void EmitReturnTypeEdge(IMethodSymbol method, string methodId, AdapterExtractionContext context, List<EdgeRecord> edges,
        (string? path, int? sl, int? sc, int? el, int? ec) location)
    {
        if (method.ReturnsVoid || method.ReturnType == null)
            return;

        var returnTypeId = context.MakeSymbolId(method.ReturnType);
        if (returnTypeId == null)
            return;

        edges.Add(context.MakeEdge(methodId, returnTypeId, nameof(EdgeKind.Returns), Provenance.FrameworkDerived, AdapterVersion, location));
    }

    private static void EmitFromServicesEdges(IMethodSymbol method, string methodId, AdapterExtractionContext context, List<EdgeRecord> edges,
        (string? path, int? sl, int? sc, int? el, int? ec) location)
    {
        foreach (var param in method.Parameters)
        {
            var hasFromServices = param.GetAttributes().Any(a => a.AttributeClass?.Name is "FromServicesAttribute" or "FromServices");
            if (!hasFromServices)
                continue;

            var paramTypeId = context.MakeSymbolId(param.Type);
            if (paramTypeId == null)
                continue;

            edges.Add(context.MakeEdge(methodId, paramTypeId, nameof(EdgeKind.References), Provenance.FrameworkDerived, AdapterVersion, location));
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

    // Same rule as ASP.NET Core's DefaultApplicationModelProvider.IsAction: an action is a
    // public, non-static, non-abstract, non-generic method that is not [NonAction] (also
    // when inherited from the method it overrides), not an object override and not
    // IDisposable.Dispose. Other methods never receive a route.
    private static bool IsAction(INamedTypeSymbol controller, IMethodSymbol method)
    {
        if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic || method.IsAbstract || method.IsGenericMethod)
            return false;

        for (var current = method; current != null; current = current.OverriddenMethod)
        {
            if (current.ContainingType.SpecialType == SpecialType.System_Object)
                return false;

            if (current.GetAttributes().Any(a => a.AttributeClass?.Name is "NonActionAttribute" or "NonAction"))
                return false;
        }

        var dispose = controller.AllInterfaces
            .FirstOrDefault(i => i.SpecialType == SpecialType.System_IDisposable)?
            .GetMembers(nameof(IDisposable.Dispose))
            .FirstOrDefault();

        return dispose == null || !SymbolEqualityComparer.Default.Equals(controller.FindImplementationForInterfaceMember(dispose), method);
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
}
