using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Workspace;

internal sealed class StringLiteralReflectionExtractor(ReflectionExtractionContext context)
{
    private const string EntityBuilderTypeNamespace = "Microsoft.EntityFrameworkCore.Metadata.Builders.";
    private const string QueryableExtensionsType = "Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions";
    private const string ControllerBaseType = "Microsoft.AspNetCore.Mvc.ControllerBase";
    private const string UrlHelperType = "Microsoft.AspNetCore.Mvc.IUrlHelper";
    private const string HtmlHelperType = "Microsoft.AspNetCore.Mvc.Rendering.IHtmlHelper";

    private static readonly HashSet<string> EntityBuilderMethods = new(StringComparer.Ordinal)
    {
        "Property", "HasOne", "HasMany", "WithOne", "WithMany", "Navigation",
        "HasForeignKey", "HasKey", "HasIndex", "Ignore"
    };

    private static readonly HashSet<string> MvcActionMethods = new(StringComparer.Ordinal)
    {
        "RedirectToAction", "CreatedAtAction", "AcceptedAtAction"
    };

    internal List<EdgeRecord> Extract(SyntaxNode root, SemanticModel semanticModel)
    {
        var edges = new List<EdgeRecord>();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            // D17: a literal is a candidate only when a sink binds it and it equals a declared
            // name in the sink's scope. There is no length cutoff and no noise filter: a string
            // that is not an identifier never equals a declared name.
            var text = literal.Token.ValueText;
            if (text.Length == 0)
                continue;

            if (!TryResolveBinding(literal, semanticModel, out var binding))
                continue;

            var matchedSymbols = ResolveCandidates(text, binding);
            if (matchedSymbols.Count == 0)
                continue;

            var sourceId = context.OwnerIdOf(literal);
            if (sourceId == null)
                continue;

            var targetIds = new List<string>(matchedSymbols.Count);
            foreach (var symbol in matchedSymbols)
            {
                var targetId = context.MakeSymbolId(symbol);
                if (targetId != null)
                    targetIds.Add(targetId);
            }

            if (targetIds.Count == 0)
                continue;

            targetIds.Sort(StringComparer.Ordinal);

            var loc = context.GetLocationInfo(literal.GetLocation());

            // D13, D134: a candidate that names its own owner is a fact like any other; the
            // readers ignore an edge from a member to itself.
            foreach (var targetId in targetIds)
                edges.Add(context.MakeEdge(sourceId, targetId, nameof(EdgeKind.ReflectionNameCandidate), ExtractorConstants.ReflectionExtractor, loc, Provenance.NameCandidate));
        }

        return edges;
    }

    private bool TryResolveBinding(LiteralExpressionSyntax literal, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        if (literal.Parent is AttributeArgumentSyntax attributeArgument)
            return TryResolveAttributeBinding(attributeArgument, semanticModel, out binding);

        if (literal.Parent is not ArgumentSyntax argument || argument.Parent is not ArgumentListSyntax argumentList)
            return false;

        switch (argumentList.Parent)
        {
            case InvocationExpressionSyntax invocation:
                return TryResolveInvocationBinding(invocation, argument, semanticModel, out binding);
            case ObjectCreationExpressionSyntax creation:
                return TryResolvePropertyChangedBinding(argument, creation, semanticModel, out binding);
            default:
                return false;
        }
    }

    private bool TryResolveInvocationBinding(InvocationExpressionSyntax invocation, ArgumentSyntax argument, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        // D36, D143: a runtime name-binding API binds only its name argument.
        if (ReflectionSinkPatterns.Resolve(invocation, semanticModel) is { } call)
        {
            if (call.Sink.TargetKind == NameTargetKind.None || !ReferenceEquals(call.NameArgument, argument))
                return false;

            var scope = call.KnownType is null ? NameScope.Global : call.DeclaredOnly ? NameScope.Declared : call.Sink.Scope;
            binding = new LiteralBinding(call.Sink.TargetKind, scope,
                call.KnownType is null ? [] : [call.KnownType], ControllersOnly: false, call.IgnoreCase);
            return true;
        }

        var method = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (method is null)
            return false;

        if (TryResolveCallerMemberNameBinding(invocation, argument, method, semanticModel, out binding))
            return true;

        if (TryResolveEntityFrameworkBinding(invocation, argument, method, semanticModel, out binding))
            return true;

        return TryResolveMvcActionBinding(invocation, argument, method, semanticModel, out binding);
    }

    private bool TryResolveCallerMemberNameBinding(InvocationExpressionSyntax invocation, ArgumentSyntax argument, IMethodSymbol method, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        var parameter = ResolveArgumentParameter(method, argument);
        if (parameter is null || parameter.Type.SpecialType != SpecialType.System_String)
            return false;

        var isCallerMemberName = parameter.GetAttributes().Any(
            attribute => attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.CallerMemberNameAttribute");
        if (!isCallerMemberName)
            return false;

        var containingType = OwnerType(context.OwnerOf(invocation));
        if (containingType is null)
            return false;

        binding = LiteralBinding.InTypeHierarchy(NameTargetKind.AnyMember, containingType);
        return true;
    }

    private static bool TryResolveEntityFrameworkBinding(InvocationExpressionSyntax invocation, ArgumentSyntax argument, IMethodSymbol method, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        var containingTypeName = method.ContainingType?.ToDisplayString() ?? "";
        var isQueryableInclude = method.Name == "Include" && containingTypeName == QueryableExtensionsType;
        var isBuilderMethod = EntityBuilderMethods.Contains(method.Name)
                              && containingTypeName.StartsWith(EntityBuilderTypeNamespace, StringComparison.Ordinal);
        if (!isQueryableInclude && !isBuilderMethod)
            return false;

        var parameter = ResolveArgumentParameter(method, argument);
        if (parameter is null || !IsStringOrStringArray(parameter.Type))
            return false;

        var receiverType = GetReceiverType(invocation, semanticModel) as INamedTypeSymbol;

        // Untyped model-snapshot builders are design-time only.
        if (receiverType is { Name: "EntityTypeBuilder", Arity: 0 })
            return false;

        var scopeTypes = receiverType is null
            ? Array.Empty<INamedTypeSymbol>()
            : receiverType.TypeArguments.OfType<INamedTypeSymbol>().ToArray();

        binding = LiteralBinding.InTypeHierarchy(NameTargetKind.PropertyOrField, scopeTypes);
        return true;
    }

    private static bool TryResolveMvcActionBinding(InvocationExpressionSyntax invocation, ArgumentSyntax argument, IMethodSymbol method, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        if (!IsMvcActionNameMethod(invocation, method, semanticModel))
            return false;

        var parameter = ResolveArgumentParameter(method, argument);
        if (parameter is null || parameter.Type.SpecialType != SpecialType.System_String)
            return false;

        if (parameter.Name is not ("action" or "actionName"))
            return false;

        binding = new LiteralBinding(NameTargetKind.Method, NameScope.Global, [], ControllersOnly: true, IgnoreCase: false);
        return true;
    }

    private static bool IsMvcActionNameMethod(InvocationExpressionSyntax invocation, IMethodSymbol method, SemanticModel semanticModel)
    {
        if (MvcActionMethods.Contains(method.Name) && IsControllerType(method.ContainingType))
            return true;

        if (method.Name == "Action" && IsUrlHelperReceiver(invocation, semanticModel))
            return true;

        if (method.Name == "ActionLink" && IsHtmlHelperReceiver(invocation, method, semanticModel))
            return true;

        return false;
    }

    private bool TryResolvePropertyChangedBinding(ArgumentSyntax argument, ObjectCreationExpressionSyntax creation, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        if (creation.ArgumentList?.Arguments.IndexOf(argument) != 0)
            return false;

        if (semanticModel.GetTypeInfo(creation).Type?.ToDisplayString() != "System.ComponentModel.PropertyChangedEventArgs")
            return false;

        var containingType = OwnerType(context.OwnerOf(creation));
        if (containingType is null)
            return false;

        binding = LiteralBinding.InTypeHierarchy(NameTargetKind.Property, containingType);
        return true;
    }

    private bool TryResolveAttributeBinding(AttributeArgumentSyntax argument, SemanticModel semanticModel, out LiteralBinding binding)
    {
        binding = default;

        if (argument.Parent is not AttributeArgumentListSyntax { Parent: AttributeSyntax attribute })
            return false;

        var constructor = semanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
        var attributeType = constructor?.ContainingType ?? semanticModel.GetTypeInfo(attribute).Type as INamedTypeSymbol;
        if (attributeType is null)
            return false;

        switch (attributeType.ToDisplayString())
        {
            case "System.ComponentModel.DataAnnotations.Schema.ForeignKeyAttribute":
            case "System.ComponentModel.DataAnnotations.Schema.InversePropertyAttribute":
            {
                if (!IsNameAttributeArgument(argument, constructor, "name", "propertyName", "property"))
                    return false;

                var containingType = OwnerType(context.OwnerOf(attribute));
                if (containingType is null)
                    return false;

                binding = LiteralBinding.InTypeHierarchy(NameTargetKind.PropertyOrField, containingType);
                return true;
            }

            case "Xunit.MemberDataAttribute":
            {
                if (!IsNameAttributeArgument(argument, constructor, "memberName"))
                    return false;

                var memberType = GetMemberDataMemberType(attribute, semanticModel) ?? OwnerType(context.OwnerOf(attribute));
                if (memberType is null)
                    return false;

                binding = LiteralBinding.InTypeHierarchy(NameTargetKind.PropertyFieldOrMethod, memberType);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool IsNameAttributeArgument(AttributeArgumentSyntax argument, IMethodSymbol? constructor, params string[] parameterNames)
    {
        var namedArgument = argument.NameEquals?.Name.Identifier.Text ?? argument.NameColon?.Name.Identifier.Text;
        if (namedArgument is not null)
            return parameterNames.Any(parameterName => string.Equals(parameterName, namedArgument, StringComparison.OrdinalIgnoreCase));

        if (constructor is null || argument.Parent is not AttributeArgumentListSyntax argumentList)
            return false;

        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0 || index >= constructor.Parameters.Length)
            return false;

        var parameter = constructor.Parameters[index];
        return parameterNames.Any(parameterName => string.Equals(parameterName, parameter.Name, StringComparison.Ordinal));
    }

    private static INamedTypeSymbol? GetMemberDataMemberType(AttributeSyntax attribute, SemanticModel semanticModel)
    {
        if (attribute.ArgumentList is null)
            return null;

        var constructor = semanticModel.GetSymbolInfo(attribute).Symbol as IMethodSymbol;

        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.Expression is not TypeOfExpressionSyntax typeOfExpression)
                continue;

            var parameterName = argument.NameEquals?.Name.Identifier.Text ?? argument.NameColon?.Name.Identifier.Text;
            if (parameterName is null)
            {
                var index = attribute.ArgumentList.Arguments.IndexOf(argument);
                parameterName = constructor is not null && index >= 0 && index < constructor.Parameters.Length
                    ? constructor.Parameters[index].Name
                    : null;
            }

            if (string.Equals(parameterName, "memberType", StringComparison.OrdinalIgnoreCase))
                return semanticModel.GetTypeInfo(typeOfExpression.Type).Type as INamedTypeSymbol;
        }

        return null;
    }

    /// <summary>
    ///     The declared symbols that <paramref name="text" /> names under <paramref name="binding" />:
    ///     the same name (ordinal, or ignoring case when the call asks for it, D21), the member kind
    ///     of the sink (D21, D143) and the scope of the sink (D36).
    /// </summary>
    private List<ISymbol> ResolveCandidates(string text, LiteralBinding binding)
    {
        IEnumerable<ISymbol> named = binding.Kind switch
        {
            NameTargetKind.Type => context.TypesByFullName(ReflectionExtractionContext.TypeNameOf(text), binding.IgnoreCase),
            NameTargetKind.Interface => context.TypesBySimpleName(text, binding.IgnoreCase)
                .Concat(context.TypesByFullName(text, binding.IgnoreCase))
                .Where(static type => type.TypeKind == TypeKind.Interface)
                .Distinct<ISymbol>(SymbolEqualityComparer.Default),
            _ => context.MembersNamed(text, binding.IgnoreCase).Where(symbol => IsKind(symbol, binding.Kind))
        };

        var scoped = new List<ISymbol>();
        foreach (var symbol in named)
        {
            if (InScope(symbol, binding))
                scoped.Add(symbol);
        }

        return scoped;
    }

    private static bool IsKind(ISymbol symbol, NameTargetKind kind)
    {
        return kind switch
        {
            NameTargetKind.NestedType => symbol is INamedTypeSymbol,
            NameTargetKind.Method => symbol is IMethodSymbol,
            NameTargetKind.Property => symbol is IPropertySymbol,
            NameTargetKind.Field => symbol is IFieldSymbol,
            NameTargetKind.Event => symbol is IEventSymbol,
            NameTargetKind.PropertyOrField => symbol is IPropertySymbol or IFieldSymbol,
            NameTargetKind.PropertyFieldOrMethod => symbol is IPropertySymbol or IFieldSymbol or IMethodSymbol,
            NameTargetKind.AnyMember => true,
            _ => false
        };
    }

    private static bool InScope(ISymbol symbol, LiteralBinding binding)
    {
        if (binding.ControllersOnly)
            return IsControllerType(symbol.ContainingType);

        return binding.Scope switch
        {
            NameScope.Global => true,
            NameScope.Declared => binding.ScopeTypes.Any(type => SameType(symbol.ContainingType, type)),
            NameScope.Hierarchy => binding.ScopeTypes.Any(type => SelfAndBaseTypes(type).Any(scope => SameType(symbol.ContainingType, scope))),
            NameScope.Interfaces => binding.ScopeTypes.Any(type => type.AllInterfaces.Any(@interface => SameType(symbol as INamedTypeSymbol, @interface))),
            _ => false
        };
    }

    private static IEnumerable<INamedTypeSymbol> SelfAndBaseTypes(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
    }

    private static bool SameType(INamedTypeSymbol? candidate, INamedTypeSymbol scope)
    {
        return candidate is not null &&
               SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, scope.OriginalDefinition);
    }

    private static IParameterSymbol? ResolveArgumentParameter(IMethodSymbol method, ArgumentSyntax argument)
    {
        if (argument.NameColon is { } nameColon)
        {
            var name = nameColon.Name.Identifier.Text;
            return method.Parameters.FirstOrDefault(parameter => parameter.Name == name);
        }

        if (argument.Parent is not ArgumentListSyntax argumentList)
            return null;

        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0)
            return null;

        if (index < method.Parameters.Length)
            return method.Parameters[index];

        var last = method.Parameters.LastOrDefault();
        return last is { IsParams: true } ? last : null;
    }

    private static bool IsStringOrStringArray(ITypeSymbol type)
    {
        return type.SpecialType == SpecialType.System_String
               || type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_String };
    }

    private static ITypeSymbol? GetReceiverType(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        return invocation.Expression is MemberAccessExpressionSyntax memberAccess
            ? semanticModel.GetTypeInfo(memberAccess.Expression).Type
            : null;
    }

    private static bool IsControllerType(INamedTypeSymbol? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == ControllerBaseType)
                return true;
        }

        return false;
    }

    private static bool IsUrlHelperReceiver(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var receiverType = GetReceiverType(invocation, semanticModel);
        return receiverType is not null
               && (receiverType.ToDisplayString() == UrlHelperType
                   || receiverType.AllInterfaces.Any(@interface => @interface.ToDisplayString() == UrlHelperType));
    }

    private static bool IsHtmlHelperReceiver(InvocationExpressionSyntax invocation, IMethodSymbol method, SemanticModel semanticModel)
    {
        if (method.ContainingType?.ToDisplayString() == HtmlHelperType)
            return true;

        var receiverType = GetReceiverType(invocation, semanticModel);
        return receiverType is not null
               && (receiverType.ToDisplayString() == HtmlHelperType
                   || receiverType.AllInterfaces.Any(@interface => @interface.ToDisplayString() == HtmlHelperType));
    }

    /// <summary>The type whose members an owner-scoped name names: the owner type, or the type of the owner member.</summary>
    private static INamedTypeSymbol? OwnerType(ISymbol? owner)
    {
        return owner as INamedTypeSymbol ?? owner?.ContainingType;
    }

    /// <summary>What a bound literal names: the kind, the scope and its types, and the case rule (D21, D36).</summary>
    private readonly record struct LiteralBinding(
        NameTargetKind Kind,
        NameScope Scope,
        IReadOnlyList<INamedTypeSymbol> ScopeTypes,
        bool ControllersOnly,
        bool IgnoreCase)
    {
        /// <summary>
        ///     D36: a name in the given types and their base types, or in every type when none is
        ///     given. The framework APIs bind inherited members (EF Core, xUnit MemberData,
        ///     PropertyChanged).
        /// </summary>
        internal static LiteralBinding InTypeHierarchy(NameTargetKind kind, params IReadOnlyList<INamedTypeSymbol> types) =>
            new(kind, types.Count == 0 ? NameScope.Global : NameScope.Hierarchy, types, ControllersOnly: false, IgnoreCase: false);
    }
}
