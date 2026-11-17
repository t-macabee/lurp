using System.Text;
using Microsoft.CodeAnalysis;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class AspNetCoreAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "aspnetcore-v3";

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

            var overridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

            for (var current = type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
            {
                foreach (var member in current.GetMembers())
                {
                    if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method)
                        continue;

                    if (overridden.Contains(method))
                        continue;

                    for (var ov = method.OverriddenMethod; ov != null; ov = ov.OverriddenMethod)
                        overridden.Add(ov);

                    if (!IsAction(type, method))
                        continue;

                    ProcessControllerAction(type, method, context, edges);
                }
            }
        }

        return edges;
    }

    private static void ProcessControllerAction(INamedTypeSymbol controller, IMethodSymbol method, AdapterExtractionContext context, List<EdgeRecord> edges)
    {
        var methodId = context.MakeSymbolId(method.OriginalDefinition);
        if (methodId == null)
            return;

        var location = SymbolEqualityComparer.Default.Equals(method.ContainingType, controller)
            ? context.LocationResolver.Resolve(method)
            : context.LocationResolver.Resolve(controller);
        foreach (var (httpMethod, template) in BuildRoutes(controller, method))
        {
            var routeSourceId = $"{GraphNodeIds.RoutePrefix}{httpMethod} /{template}";
            edges.Add(context.MakeEdge(routeSourceId, methodId, nameof(EdgeKind.RoutesTo), Provenance.FrameworkDerived, AdapterVersion, location,
                sourceNodeKind: GraphNodeKind.Route));
        }
    }

    private static bool IsController(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Class ||
            type.ContainingType != null ||
            type.DeclaredAccessibility != Accessibility.Public ||
            type.IsAbstract ||
            type.IsGenericType)
        {
            return false;
        }

        var hasControllerAttr = false;

        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(type))
        {
            if (FrameworkTypes.Is(attr.AttributeClass, "Microsoft.AspNetCore.Mvc.NonControllerAttribute"))
                return false;

            if (FrameworkTypes.Is(attr.AttributeClass, "Microsoft.AspNetCore.Mvc.ControllerAttribute"))
                hasControllerAttr = true;
        }

        return hasControllerAttr || type.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase);
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
        }

        if (FrameworkTypes.GetAttributesWithInheritance(method).Any(a => FrameworkTypes.Is(a.AttributeClass, "Microsoft.AspNetCore.Mvc.NonActionAttribute")))
            return false;

        var dispose = controller.AllInterfaces
            .FirstOrDefault(i => i.SpecialType == SpecialType.System_IDisposable)?
            .GetMembers(nameof(IDisposable.Dispose))
            .FirstOrDefault();

        return dispose == null || !SymbolEqualityComparer.Default.Equals(controller.FindImplementationForInterfaceMember(dispose), method);
    }

    private static List<(string Method, string Template)> BuildRoutes(INamedTypeSymbol controller, IMethodSymbol action)
    {
        var controllerSelectors = CreateSelectors(controller);
        var actionSelectors = CreateSelectors(action);

        var controllerToken = controller.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            ? controller.Name[..^"Controller".Length]
            : controller.Name;

        var actionToken = GetActionName(action);
        var areaToken = GetAreaName(action) ?? GetAreaName(controller);

        var routes = new List<(string Method, string Template)>();

        foreach (var cSel in controllerSelectors)
        {
            foreach (var aSel in actionSelectors)
            {
                var combined = CombineTemplates(cSel.Template, aSel.Template);
                if (combined == null)
                    continue;

                var replaced = ReplaceTokens(combined, controllerToken, actionToken, areaToken);
                if (replaced == null)
                    continue;

                if (aSel.HttpMethods.Count > 0)
                {
                    foreach (var httpMethod in aSel.HttpMethods)
                        routes.Add((httpMethod, replaced));
                }
                else
                {
                    routes.Add(("ANY", replaced));
                }
            }
        }

        return routes;
    }

    private sealed record RouteSelector(string? Template, IReadOnlyList<string> HttpMethods);

    private static List<RouteSelector> CreateSelectors(ISymbol symbol)
    {
        var nonSilent = new List<(string? Template, List<string> Methods)>();
        var silentMethods = new List<string>();

        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(symbol))
        {
            if (!TryReadRouteProvider(attr, out var template, out var hasNameOrOrder, out var methods))
                continue;

            if (template == null && !hasNameOrOrder)
            {
                foreach (var m in methods)
                {
                    if (!silentMethods.Contains(m, StringComparer.Ordinal))
                        silentMethods.Add(m);
                }
            }
            else
            {
                nonSilent.Add((template, methods));
            }
        }

        var selectors = new List<RouteSelector>();

        if (nonSilent.Count == 0)
        {
            selectors.Add(new RouteSelector(null, silentMethods));
            return selectors;
        }

        var hasMethodlessNonSilent = false;
        foreach (var (template, methods) in nonSilent)
        {
            if (methods.Count == 0)
            {
                hasMethodlessNonSilent = true;
                selectors.Add(new RouteSelector(template, silentMethods.Count > 0 ? silentMethods : methods));
            }
            else
            {
                selectors.Add(new RouteSelector(template, methods));
            }
        }

        if (silentMethods.Count > 0 && !hasMethodlessNonSilent)
            selectors.Add(new RouteSelector(null, silentMethods));

        return selectors;
    }

    private static bool TryReadRouteProvider(AttributeData attr, out string? template, out bool hasNameOrOrder, out List<string> methods)
    {
        template = null;
        hasNameOrOrder = false;
        methods = [];

        var attrClass = attr.AttributeClass;
        if (attrClass == null)
            return false;

        if (FrameworkTypes.Is(attrClass, "Microsoft.AspNetCore.Mvc.RouteAttribute"))
        {
            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string t)
                template = t;
            hasNameOrOrder = HasNameOrOrder(attr);
            return true;
        }

        if (FrameworkTypes.Is(attrClass, "Microsoft.AspNetCore.Mvc.AcceptVerbsAttribute"))
        {
            if (attr.ConstructorArguments.Length > 0)
            {
                var arg = attr.ConstructorArguments[0];
                if (arg.Kind == TypedConstantKind.Array)
                {
                    foreach (var item in arg.Values)
                    {
                        if (item.Value is string m && !string.IsNullOrEmpty(m))
                            methods.Add(m.ToUpperInvariant());
                    }
                }
                else if (arg.Value is string m && !string.IsNullOrEmpty(m))
                {
                    methods.Add(m.ToUpperInvariant());
                }
            }

            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "Route" && named.Value.Value is string r)
                    template = r;
                else if (named.Key is "Name" or "Order" && named.Value.Value != null)
                    hasNameOrOrder = true;
            }

            return true;
        }

        if (FrameworkTypes.DerivesFrom(attrClass, "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute"))
        {
            var fixedMethod = GetKnownHttpMethod(attrClass);
            if (fixedMethod == null)
                return false;

            methods.Add(fixedMethod);
            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is string t)
                template = t;
            hasNameOrOrder = HasNameOrOrder(attr);
            return true;
        }

        return false;
    }

    private static bool HasNameOrOrder(AttributeData attr)
    {
        return attr.NamedArguments.Any(named => named.Key is "Name" or "Order" && named.Value.Value != null);
    }

    private static string? GetKnownHttpMethod(INamedTypeSymbol attrClass)
    {
        for (var cur = attrClass; cur != null; cur = cur.BaseType)
        {
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpGetAttribute")) return "GET";
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpPostAttribute")) return "POST";
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpPutAttribute")) return "PUT";
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute")) return "DELETE";
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpPatchAttribute")) return "PATCH";
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpHeadAttribute")) return "HEAD";
            if (FrameworkTypes.Is(cur, "Microsoft.AspNetCore.Mvc.HttpOptionsAttribute")) return "OPTIONS";
        }
        return null;
    }

    private static string? CombineTemplates(string? left, string? right)
    {
        if (left == null && right == null)
            return null;

        if (right != null && (right.StartsWith("~/", StringComparison.Ordinal) || right.StartsWith('/')))
            return TrimTemplateSlashes(right);

        var cleanLeft = left != null ? TrimTemplateSlashes(left) : string.Empty;
        var cleanRight = right != null ? TrimTemplateSlashes(right) : string.Empty;

        if (cleanLeft.Length == 0)
            return cleanRight;
        if (cleanRight.Length == 0)
            return cleanLeft;
        return $"{cleanLeft}/{cleanRight}";
    }

    private static string TrimTemplateSlashes(string template)
    {
        if (template.StartsWith("~/", StringComparison.Ordinal))
            template = template[2..];
        return template.Trim('/');
    }

    private static string GetActionName(IMethodSymbol action)
    {
        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(action))
        {
            if (FrameworkTypes.Is(attr.AttributeClass, "Microsoft.AspNetCore.Mvc.ActionNameAttribute") &&
                attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string name)
            {
                return name;
            }
        }

        return action.Name.EndsWith("Async", StringComparison.Ordinal)
            ? action.Name[..^"Async".Length]
            : action.Name;
    }

    private static string? GetAreaName(ISymbol symbol)
    {
        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(symbol))
        {
            if (FrameworkTypes.Is(attr.AttributeClass, "Microsoft.AspNetCore.Mvc.AreaAttribute") &&
                attr.ConstructorArguments.Length > 0 &&
                attr.ConstructorArguments[0].Value is string area)
            {
                return area;
            }
        }

        return null;
    }

    private static string? ReplaceTokens(string template, string controller, string action, string? area)
    {
        var sb = new StringBuilder(template.Length);
        var i = 0;

        while (i < template.Length)
        {
            var c = template[i];
            if (c == '[' && i + 1 < template.Length && template[i + 1] == '[')
            {
                sb.Append('[');
                i += 2;
                continue;
            }

            if (c == ']' && i + 1 < template.Length && template[i + 1] == ']')
            {
                sb.Append(']');
                i += 2;
                continue;
            }

            if (c == '[')
            {
                var close = template.IndexOf(']', i + 1);
                if (close < 0)
                    return null;

                var token = template[(i + 1)..close];
                if (string.Equals(token, "controller", StringComparison.OrdinalIgnoreCase))
                    sb.Append(controller);
                else if (string.Equals(token, "action", StringComparison.OrdinalIgnoreCase))
                    sb.Append(action);
                else if (string.Equals(token, "area", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrEmpty(area))
                        return null;
                    sb.Append(area);
                }
                else
                {
                    return null;
                }

                i = close + 1;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }
}
