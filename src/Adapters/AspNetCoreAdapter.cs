using System.Text;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Adapters;

public sealed class AspNetCoreAdapter : IFrameworkAdapter
{
    private const string AdapterVersion = "aspnetcore-v4";

    public string Name => "ASP.NET Core";
    public string Version => AdapterVersion;
    public string Description => "ASP.NET Core controller action routes and middleware";

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

        ExtractMiddleware(context, edges);
        return edges;
    }

    private static void ExtractMiddleware(AdapterExtractionContext context, List<EdgeRecord> edges)
    {
        const string extensions = "Microsoft.AspNetCore.Builder.UseMiddlewareExtensions";
        if (context.Compilation.GetTypeByMetadataName(extensions) == null)
            return;

        foreach (var (owner, node) in context.Bodies())
        {
            if (context.MakeSymbolId(owner) is not { } ownerId)
                continue;

            var model = context.GetSemanticModel(node.SyntaxTree);
            foreach (var root in ExecutableRoots.Operations(node, model))
            foreach (var call in root.DescendantsAndSelf().OfType<IInvocationOperation>())
            {
                if (call.TargetMethod.Name != "UseMiddleware" ||
                    !FrameworkTypes.Is(FrameworkTypes.DeclaringType(call.TargetMethod), extensions))
                    continue;

                var middleware = call.TargetMethod.TypeArguments.FirstOrDefault() ??
                    (call.Arguments.FirstOrDefault(a => a.Parameter?.Name == "middleware")?.Value as ITypeOfOperation)?.TypeOperand;
                if (middleware is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: false } type ||
                    !context.IsSolutionSymbol(type) || context.MakeSymbolId(type) is not { } typeId)
                    continue;

                var location = context.LocationResolver.Resolve(call.Syntax.GetLocation());
                edges.Add(context.MakeEdge(ownerId, typeId, nameof(EdgeKind.Constructs),
                    Provenance.FrameworkDerived, AdapterVersion, location));

                var methods = MiddlewareInvokers(type);
                foreach (var method in methods.Concat(type.InstanceConstructors.Where(c => !c.IsImplicitlyDeclared &&
                             c.DeclaredAccessibility == Accessibility.Public)))
                {
                    if (context.MakeSymbolId(method) is { } methodId)
                        edges.Add(context.MakeEdge(ownerId, methodId, nameof(EdgeKind.Calls),
                            Provenance.FrameworkDerived, AdapterVersion, location));
                }
            }
        }
    }

    private static IEnumerable<IMethodSymbol> MiddlewareInvokers(INamedTypeSymbol type)
    {
        var contract = type.AllInterfaces.FirstOrDefault(i => FrameworkTypes.Is(i, "Microsoft.AspNetCore.Http.IMiddleware"));
        if (contract != null)
        {
            if (contract.GetMembers("InvokeAsync").FirstOrDefault() is { } member &&
                type.FindImplementationForInterfaceMember(member) is IMethodSymbol method)
                yield return method;
            yield break;
        }

        var overridden = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.Name is not ("Invoke" or "InvokeAsync") || method.IsStatic ||
                    method.DeclaredAccessibility != Accessibility.Public || overridden.Contains(method))
                    continue;

                for (var parent = method.OverriddenMethod; parent != null; parent = parent.OverriddenMethod)
                    overridden.Add(parent);

                if (!method.IsGenericMethod && method.Parameters.FirstOrDefault() is { } parameter &&
                    FrameworkTypes.Is(parameter.Type, "Microsoft.AspNetCore.Http.HttpContext") &&
                    FrameworkTypes.DerivesFrom(method.ReturnType, "System.Threading.Tasks.Task"))
                    yield return method;
            }
        }
    }

    private static void ProcessControllerAction(INamedTypeSymbol controller, IMethodSymbol method, AdapterExtractionContext context, List<EdgeRecord> edges)
    {
        var methodId = context.MakeSymbolId(method.OriginalDefinition);
        if (methodId == null)
            return;

        var location = SymbolEqualityComparer.Default.Equals(method.ContainingType, controller)
            ? context.LocationResolver.Resolve(method)
            : context.LocationResolver.Resolve(controller);
        foreach (var (httpMethod, template) in BuildRoutes(controller, method, context))
        {
            var routeSourceId = $"{GraphNodeIds.RoutePrefix}{httpMethod} /{template}";
            edges.Add(context.MakeEdge(routeSourceId, methodId, nameof(EdgeKind.RoutesTo), Provenance.FrameworkDerived, AdapterVersion, location,
                sourceIsBoundary: true));
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

    private static List<(string Method, string Template)> BuildRoutes(INamedTypeSymbol controller, IMethodSymbol action, AdapterExtractionContext context)
    {
        var controllerSelectors = CreateSelectors(controller, context);
        var actionSelectors = CreateSelectors(action, context);

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

    private static List<RouteSelector> CreateSelectors(ISymbol symbol, AdapterExtractionContext context)
    {
        var nonSilent = new List<(string? Template, List<string> Methods)>();
        var silentMethods = new List<string>();

        foreach (var attr in FrameworkTypes.GetAttributesWithInheritance(symbol))
        {
            if (!TryReadRouteProvider(attr, context, out var template, out var hasNameOrOrder, out var methods))
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

    private static bool TryReadRouteProvider(AttributeData attr, AdapterExtractionContext context, out string? template, out bool hasNameOrOrder, out List<string> methods)
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
            if (TryReadHttpMethodConstructor(attr, context, out var constructorTemplate, out var verbs))
                template = constructorTemplate;
            else
                TryReadFallbackConstructorArguments(RouteArguments(attr), out template, out verbs);

            if (fixedMethod != null)
                methods.Add(fixedMethod);
            else if (verbs is { Length: > 0 })
                methods.AddRange(verbs);
            else
                methods.Add("CUSTOM");

            hasNameOrOrder = HasNameOrOrder(attr);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Reads the arguments that reached a metadata-only HTTP attribute constructor.
    ///     The first argument is either the template or the verbs array.
    /// </summary>
    private static void TryReadFallbackConstructorArguments(
        object?[] arguments, out string? template, out string[]? verbs)
    {
        template = null;
        verbs = null;
        if (arguments.Length == 0)
            return;

        var first = arguments[0];
        if (first is object?[] array)
        {
            verbs = array
                .Select(value => value as string)
                .Where(verb => !string.IsNullOrEmpty(verb))
                .Select(verb => verb!.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (arguments.Length > 1)
                template = arguments[1] as string;
        }
        else
        {
            template = first as string;
        }
    }

    private static bool TryReadHttpMethodConstructor(
        AttributeData attribute, AdapterExtractionContext context, out string? template, out string[]? verbs)
    {
        const string httpMethodAttribute = "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute";
        template = null;
        verbs = null;
        var constructor = attribute.AttributeConstructor;
        var arguments = RouteArguments(attribute);

        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        while (constructor != null && visited.Add(constructor))
        {
            if (constructor.DeclaringSyntaxReferences.IsEmpty && !context.IsSolutionSymbol(constructor.ContainingType) &&
                !FrameworkTypes.Is(constructor.ContainingType, httpMethodAttribute) &&
                !IsBuiltInHttpMethodAttribute(constructor.ContainingType))
            {
                TryReadFallbackConstructorArguments(arguments, out template, out verbs);
                return true;
            }
            if (FrameworkTypes.Is(constructor.ContainingType, httpMethodAttribute))
            {
                verbs = (arguments.FirstOrDefault() as object?[])?.OfType<string>()
                    .Where(verb => verb.Length > 0).Select(verb => verb.ToUpperInvariant())
                    .Distinct(StringComparer.Ordinal).ToArray();
                template = arguments.ElementAtOrDefault(1) as string;
                return true;
            }

            if (constructor.DeclaringSyntaxReferences.IsEmpty && IsBuiltInHttpMethodAttribute(constructor.ContainingType))
            {
                template = arguments.FirstOrDefault() as string;
                return true;
            }

            var initializer = constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() switch
            {
                ConstructorDeclarationSyntax declaration => (SyntaxNode?)declaration.Initializer,
                TypeDeclarationSyntax declaration => declaration.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault(),
                _ => null
            };
            if (initializer == null)
            {
                if (!context.IsSolutionSymbol(constructor.ContainingType) ||
                    SelectImplicitBaseConstructor(constructor) is not { } baseConstructor)
                    return false;

                constructor = baseConstructor;
                arguments = ReadImplicitConstructorArguments(baseConstructor);
                continue;
            }

            if (context.GetDeclaringSemanticModel(initializer.SyntaxTree)?.GetOperation(initializer) is not IInvocationOperation call)
                return false;

            var nextArguments = new object?[call.TargetMethod.Parameters.Length];
            foreach (var argument in call.Arguments)
            {
                if (argument.Parameter is { } parameter)
                    nextArguments[parameter.Ordinal] = ReadRouteArgument(argument.Value, arguments, context);
            }

            constructor = call.TargetMethod;
            arguments = nextArguments;
        }

        return false;
    }

    private static object?[] RouteArguments(AttributeData attribute) =>
        attribute.ConstructorArguments.Select(constant => constant.Kind == TypedConstantKind.Array
            ? constant.IsNull ? null : constant.Values.Select(c => c.Value).ToArray()
            : constant.Value).ToArray();

    private static bool IsBuiltInHttpMethodAttribute(INamedTypeSymbol type)
    {
        return FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpGetAttribute") ||
               FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpPostAttribute") ||
               FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpPutAttribute") ||
               FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute") ||
               FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpPatchAttribute") ||
               FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpHeadAttribute") ||
               FrameworkTypes.Is(type, "Microsoft.AspNetCore.Mvc.HttpOptionsAttribute");
    }

    /// <summary>
    ///     The base constructor that C# selects for a constructor without an initializer:
    ///     the accessible parameterless constructor, or else the only accessible constructor
    ///     whose parameters are all optional or <c>params</c> (ECMA-334, 12.6.4).
    /// </summary>
    private static IMethodSymbol? SelectImplicitBaseConstructor(IMethodSymbol constructor)
    {
        if (constructor.ContainingType.BaseType is not { } baseType)
            return null;

        var accessible = baseType.InstanceConstructors
            .Where(candidate => IsAccessibleFrom(candidate, constructor.ContainingType))
            .ToArray();

        var parameterless = accessible.FirstOrDefault(candidate => candidate.Parameters.Length == 0);
        if (parameterless != null)
            return parameterless;

        var applicable = accessible
            .Where(candidate => candidate.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
            .ToArray();
        return applicable.Length == 1 ? applicable[0] : null;
    }

    /// <summary>
    ///     Whether <paramref name="containingType" /> can invoke this base constructor. The type is
    ///     derived from the constructor's type, so protected access is enough. Internal access also
    ///     needs the same assembly identity or an <c>InternalsVisibleTo</c> grant. The decision uses
    ///     the declared accessibility only: the two symbols can come from different compilations,
    ///     where <c>Compilation.IsSymbolAccessibleWithin</c> rejects the other compilation's symbols.
    /// </summary>
    private static bool IsAccessibleFrom(IMethodSymbol constructor, INamedTypeSymbol containingType)
    {
        return constructor.DeclaredAccessibility switch
        {
            Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal => true,
            Accessibility.Internal or Accessibility.ProtectedAndInternal =>
                constructor.ContainingAssembly is { } assembly &&
                containingType.ContainingAssembly is { } containingAssembly &&
                (assembly.Identity.Equals(containingAssembly.Identity) ||
                 assembly.GivesAccessTo(containingAssembly)),
            _ => false
        };
    }

    private static object?[] ReadImplicitConstructorArguments(IMethodSymbol constructor)
    {
        var arguments = new object?[constructor.Parameters.Length];
        for (var i = 0; i < constructor.Parameters.Length; i++)
        {
            var parameter = constructor.Parameters[i];
            arguments[i] = parameter.IsParams
                ? Array.Empty<object?>()
                : parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue : null;
        }

        return arguments;
    }

    private static object? ReadRouteArgument(
        IOperation value, object?[] arguments, AdapterExtractionContext context, HashSet<IFieldSymbol>? fields = null)
    {
        switch (value)
        {
            case { ConstantValue.HasValue: true }:
                return value.ConstantValue.Value;
            case IConversionOperation conversion:
                return ReadRouteArgument(conversion.Operand, arguments, context, fields);
            case IParameterReferenceOperation parameter:
                return arguments.ElementAtOrDefault(parameter.Parameter.Ordinal);
            case IArrayCreationOperation { Initializer: { } initializer }:
                return initializer.ElementValues.Select(element => ReadRouteArgument(element, arguments, context, fields)).ToArray();
            case ICollectionExpressionOperation collection:
                return collection.Elements.Select(element => ReadRouteArgument(element, arguments, context, fields)).ToArray();
            case IFieldReferenceOperation { Field.IsStatic: true, Field.IsReadOnly: true } field:
                fields ??= new HashSet<IFieldSymbol>(SymbolEqualityComparer.Default);
                if (field.Field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not VariableDeclaratorSyntax
                    { Initializer.Value: { } expression } ||
                    context.GetDeclaringSemanticModel(expression.SyntaxTree)?.GetOperation(expression) is not { } fieldValue ||
                    !fields.Add(field.Field))
                    return null;

                try
                {
                    return ReadRouteArgument(fieldValue, [], context, fields);
                }
                finally
                {
                    fields.Remove(field.Field);
                }
        }

        return null;
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
