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
        var seen = new HashSet<(string source, string target, string kind)>();

        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;

            var text = literal.Token.ValueText;
            if (string.IsNullOrEmpty(text) || text.Length < 3)
                continue;

            if (IsNoiseString(text))
                continue;

            if (!TryResolveBinding(literal, semanticModel, out var binding))
                continue;

            var matchedSymbols = ResolveCandidates(text, binding);
            if (matchedSymbols.Count == 0)
                continue;

            var sourceId = context.GetContainingMemberSymbolId(literal, semanticModel);
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

            foreach (var targetId in targetIds)
            {
                if (sourceId == targetId)
                    continue;

                var key = (sourceId, targetId, nameof(EdgeKind.ReflectionNameCandidate));
                if (!seen.Add(key))
                    continue;

                edges.Add(new EdgeRecord
                {
                    SourceSymbolId = sourceId,
                    TargetSymbolId = targetId,
                    Kind = nameof(EdgeKind.ReflectionNameCandidate),
                    Provenance = Provenance.NameCandidate,
                    SnapshotId = context.SnapshotId,
                    ExtractorVersion = ExtractorConstants.ReflectionExtractor,
                    SourceDocumentPath = loc.path,
                    SourceStartLine = loc.startLine,
                    SourceStartColumn = loc.startColumn,
                    SourceEndLine = loc.endLine,
                    SourceEndColumn = loc.endColumn,
                    IsCrossGenerated = context.IsGenerated(loc.path)
                });
            }
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

        var reflectionSink = ReflectionSinkPatterns.Resolve(invocation, semanticModel);
        if (reflectionSink is { } sink && IsReflectionNameArgument(invocation, argument, sink))
        {
            var scopeTypes = new List<INamedTypeSymbol>();
            if (sink.KnownType is not null)
                scopeTypes.Add(sink.KnownType);

            binding = new LiteralBinding(ToTargetKind(sink.TargetKind), scopeTypes, ControllersOnly: false);
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

    private static bool IsReflectionNameArgument(InvocationExpressionSyntax invocation, ArgumentSyntax argument, ReflectionSinkPattern sink)
    {
        var arguments = invocation.ArgumentList.Arguments;

        if (sink.Pattern == "Activator.CreateInstance")
        {
            for (var i = arguments.Count - 1; i >= 0; i--)
            {
                if (arguments[i].Expression is LiteralExpressionSyntax candidate && candidate.IsKind(SyntaxKind.StringLiteralExpression))
                    return ReferenceEquals(arguments[i], argument);
            }

            return false;
        }

        return arguments.Count > 0 && ReferenceEquals(arguments[0], argument);
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

        var containingType = context.GetContainingMemberSymbol(invocation, semanticModel)?.ContainingType;
        if (containingType is null)
            return false;

        binding = new LiteralBinding(NameCandidateTargetKind.Member, new[] { containingType }, ControllersOnly: false);
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

        binding = new LiteralBinding(NameCandidateTargetKind.Member, scopeTypes, ControllersOnly: false);
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

        binding = new LiteralBinding(NameCandidateTargetKind.Member, Array.Empty<INamedTypeSymbol>(), ControllersOnly: true);
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

        var containingType = context.GetContainingMemberSymbol(creation, semanticModel)?.ContainingType;
        if (containingType is null)
            return false;

        binding = new LiteralBinding(NameCandidateTargetKind.Member, new[] { containingType }, ControllersOnly: false);
        return true;
    }

    private static bool TryResolveAttributeBinding(AttributeArgumentSyntax argument, SemanticModel semanticModel, out LiteralBinding binding)
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

                var containingType = GetAttributedContainingType(attribute, semanticModel);
                if (containingType is null)
                    return false;

                binding = new LiteralBinding(NameCandidateTargetKind.Member, new[] { containingType }, ControllersOnly: false);
                return true;
            }

            case "Xunit.MemberDataAttribute":
            {
                if (!IsNameAttributeArgument(argument, constructor, "memberName"))
                    return false;

                var memberType = GetMemberDataMemberType(attribute, semanticModel) ?? GetAttributedContainingType(attribute, semanticModel);
                if (memberType is null)
                    return false;

                binding = new LiteralBinding(NameCandidateTargetKind.Member, new[] { memberType }, ControllersOnly: false);
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

    private static INamedTypeSymbol? GetAttributedContainingType(AttributeSyntax attribute, SemanticModel semanticModel)
    {
        if (attribute.Parent is not AttributeListSyntax { Parent: { } declaration })
            return null;

        return declaration switch
        {
            PropertyDeclarationSyntax property => (semanticModel.GetDeclaredSymbol(property) as IPropertySymbol)?.ContainingType,
            FieldDeclarationSyntax field => field.Declaration.Variables.Count == 0
                ? null
                : (semanticModel.GetDeclaredSymbol(field.Declaration.Variables[0]) as IFieldSymbol)?.ContainingType,
            EventDeclarationSyntax eventDeclaration => (semanticModel.GetDeclaredSymbol(eventDeclaration) as IEventSymbol)?.ContainingType,
            EventFieldDeclarationSyntax eventField => eventField.Declaration.Variables.Count == 0
                ? null
                : (semanticModel.GetDeclaredSymbol(eventField.Declaration.Variables[0]) as IEventSymbol)?.ContainingType,
            MethodDeclarationSyntax method => (semanticModel.GetDeclaredSymbol(method) as IMethodSymbol)?.ContainingType,
            ConstructorDeclarationSyntax constructor => (semanticModel.GetDeclaredSymbol(constructor) as IMethodSymbol)?.ContainingType,
            ClassDeclarationSyntax @class => semanticModel.GetDeclaredSymbol(@class) as INamedTypeSymbol,
            StructDeclarationSyntax @struct => semanticModel.GetDeclaredSymbol(@struct) as INamedTypeSymbol,
            RecordDeclarationSyntax record => semanticModel.GetDeclaredSymbol(record) as INamedTypeSymbol,
            _ => null
        };
    }

    private IReadOnlyList<ISymbol> ResolveCandidates(string text, LiteralBinding binding)
    {
        List<ISymbol>? candidates = binding.TargetKind switch
        {
            NameCandidateTargetKind.Member => context.MembersByName.TryGetValue(text, out var members) ? members : null,
            NameCandidateTargetKind.Type => ResolveTypeCandidates(text),
            NameCandidateTargetKind.NestedType => context.MembersByName.TryGetValue(text, out var nested)
                ? nested.Where(static symbol => symbol is INamedTypeSymbol).ToList()
                : null,
            _ => null
        };

        if (candidates is null || candidates.Count == 0)
            return [];

        if (binding.ScopeTypes.Count == 0 && !binding.ControllersOnly)
            return candidates;

        var scoped = new List<ISymbol>(candidates.Count);
        foreach (var symbol in candidates)
        {
            var containingType = symbol.ContainingType;
            if (containingType is null)
                continue;

            if (binding.ControllersOnly)
            {
                if (IsControllerType(containingType))
                    scoped.Add(symbol);
                continue;
            }

            if (binding.ScopeTypes.Any(scope => SymbolEqualityComparer.Default.Equals(containingType, scope)))
                scoped.Add(symbol);
        }

        return scoped;
    }

    private List<ISymbol>? ResolveTypeCandidates(string text)
    {
        if (context.TypesByName.TryGetValue(text, out var types))
            return types;

        return context.MembersByName.TryGetValue(text, out var members)
            ? members.Where(static symbol => symbol is INamedTypeSymbol).ToList()
            : null;
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

    private static NameCandidateTargetKind ToTargetKind(ReflectionSinkTargetKind kind)
    {
        return kind switch
        {
            ReflectionSinkTargetKind.Type => NameCandidateTargetKind.Type,
            ReflectionSinkTargetKind.NestedType => NameCandidateTargetKind.NestedType,
            _ => NameCandidateTargetKind.Member
        };
    }

    private static bool IsNoiseString(string text)
    {
        if (text.All(char.IsDigit))
            return true;
        if (text.Contains(' ') && !text.Contains('.') && !IsPascalCase(text) && !IsCamelCase(text))
            return true;
        return false;
    }

    private static bool IsPascalCase(string text)
    {
        return text.Length > 0 && char.IsUpper(text[0]) && text.Any(char.IsLower);
    }

    private static bool IsCamelCase(string text)
    {
        return text.Length > 0 && char.IsLower(text[0]) && text.Any(char.IsUpper);
    }

    private enum NameCandidateTargetKind
    {
        Member,
        Type,
        NestedType
    }

    private readonly record struct LiteralBinding(
        NameCandidateTargetKind TargetKind,
        IReadOnlyList<INamedTypeSymbol> ScopeTypes,
        bool ControllersOnly);
}