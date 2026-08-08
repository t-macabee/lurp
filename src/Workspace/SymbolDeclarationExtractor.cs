using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Lurp.Storage;
using SymKind = Lurp.Storage.IndexedSymbolKind;

namespace Lurp.Workspace;

internal sealed partial class SymbolDeclarationExtractor(SymbolExtractionContext context, Action<string>? logWarning = null)
{
    private readonly Action<string>? _logWarning = logWarning;

    [GeneratedRegex(@"\bGeneratedCode\s*\(\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedCodeToolPattern();

    internal List<SymbolDeclaration> ExtractAll()
    {
        var results = new List<SymbolDeclaration>();

        foreach (var typeSymbol in ExtractionUtils.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace))
        {
            ExtractTypeDeclarations(typeSymbol, results);
        }

        return results;
    }

    private void ExtractTypeDeclarations(INamedTypeSymbol typeSymbol, List<SymbolDeclaration> results)
    {
        AddSymbolDeclarations(typeSymbol, results);

        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            ExtractTypeDeclarations(nestedType, results);
        }

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue;

            AddSymbolDeclarations(member, results);
        }
    }

    private void AddSymbolDeclarations(ISymbol symbol, List<SymbolDeclaration> results)
    {
        var docCommentId = symbol.GetDocumentationCommentId();
        if (string.IsNullOrEmpty(docCommentId))
            return;

        var fqn = BuildFullyQualifiedName(symbol);
        var kind = MapKind(symbol);
        var metadataJson = BuildMetadataJson(symbol);

        var symbolId = new SymbolId(docCommentId, context.AssemblyIdentity, fqn);
        bool isPartial = symbol is INamedTypeSymbol typeSymbol && typeSymbol.DeclaringSyntaxReferences.Length > 1;

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var syntaxNode = syntaxRef.GetSyntax();
            var syntaxTree = syntaxRef.SyntaxTree;

            if (!context.IsInScope(syntaxTree))
                continue;

            IndexTrace.TreeWalk("SymbolDeclaration", "", syntaxTree.FilePath);

            var documentId = context.ResolveDocumentId(syntaxTree);
            if (documentId == null)
                continue;

            if (!context.DocumentVersions.TryGetValue(documentId.Value, out var versionId))
                continue;

            if (!context.DocumentContents.TryGetValue(documentId.Value, out var contentInfo))
                continue;

            var encoding = DeclarationSpanComputer.GetEncoding(contentInfo.Encoding);
            var sourceText = syntaxTree.GetText();
            var sourceString = sourceText.ToString();

            var (fullSpan, signatureSpan, bodySpan, nameSpan) = DeclarationSpanComputer.ComputeSpans(syntaxNode, sourceString, encoding);

            var isGenerated = context.GeneratedDocuments.Contains(documentId.Value);
            string? generatorIdentity = null;
            if (isGenerated && context.DocumentContents.TryGetValue(documentId.Value, out var genDocContent))
            {
                generatorIdentity = DeriveGeneratorIdentity(genDocContent.Content, genDocContent.Encoding);
            }

            results.Add(new SymbolDeclaration
            {
                SymbolId = symbolId,
                Kind = kind,
                DocumentVersionId = versionId.ToString(),
                FullSpan = fullSpan,
                SignatureSpan = signatureSpan,
                BodySpan = bodySpan,
                NameSpan = nameSpan,
                IsPartial = isPartial,
                MetadataJson = metadataJson,
                IsGenerated = isGenerated,
                GeneratorIdentity = generatorIdentity,
            });
        }
    }

    private static string BuildFullyQualifiedName(ISymbol symbol)
    {
        var name = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (symbol is not INamedTypeSymbol && symbol.ContainingType != null)
        {
            var typeFqn = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return $"{typeFqn}.{name}";
        }

        return name;
    }

    private static SymKind MapKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            Microsoft.CodeAnalysis.SymbolKind.Namespace => SymKind.Namespace,
            Microsoft.CodeAnalysis.SymbolKind.NamedType => SymKind.Type,
            Microsoft.CodeAnalysis.SymbolKind.Method => SymKind.Method,
            Microsoft.CodeAnalysis.SymbolKind.Property => SymKind.Property,
            Microsoft.CodeAnalysis.SymbolKind.Field => SymKind.Field,
            Microsoft.CodeAnalysis.SymbolKind.Event => SymKind.Event,
            Microsoft.CodeAnalysis.SymbolKind.Parameter => SymKind.Parameter,
            Microsoft.CodeAnalysis.SymbolKind.Local => SymKind.Local,
            Microsoft.CodeAnalysis.SymbolKind.RangeVariable => SymKind.RangeVariable,
            Microsoft.CodeAnalysis.SymbolKind.ArrayType => SymKind.ArrayType,
            Microsoft.CodeAnalysis.SymbolKind.PointerType => SymKind.PointerType,
            Microsoft.CodeAnalysis.SymbolKind.TypeParameter => SymKind.TypeParameter,
            _ => SymKind.Unknown,
        };
    }

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
                        SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    private static string? BuildMetadataJson(ISymbol symbol)
    {
        var metadata = new Dictionary<string, object?>();

        if (symbol is IMethodSymbol method)
        {
            metadata[SymbolMetadataKeys.ReturnType] = method.ReturnType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata[SymbolMetadataKeys.IsAbstract] = method.IsAbstract;
            metadata[SymbolMetadataKeys.IsVirtual] = method.IsVirtual;
            metadata[SymbolMetadataKeys.IsOverride] = method.IsOverride;
            metadata[SymbolMetadataKeys.IsStatic] = method.IsStatic;
            metadata[SymbolMetadataKeys.IsAsync] = method.IsAsync;
            metadata[SymbolMetadataKeys.Accessibility] = method.DeclaredAccessibility.ToString();
            metadata[SymbolMetadataKeys.Arity] = method.Arity;
            metadata[SymbolMetadataKeys.IsExtensionMethod] = method.IsExtensionMethod;
            metadata[SymbolMetadataKeys.Signature] = method.ToDisplayString(SignatureFormat);
        }
        else if (symbol is INamedTypeSymbol type)
        {
            metadata[SymbolMetadataKeys.TypeKind] = type.TypeKind.ToString();
            metadata[SymbolMetadataKeys.IsAbstract] = type.IsAbstract;
            metadata[SymbolMetadataKeys.IsStatic] = type.IsStatic;
            metadata[SymbolMetadataKeys.IsRecord] = type.IsRecord;
            metadata[SymbolMetadataKeys.Accessibility] = type.DeclaredAccessibility.ToString();
            metadata[SymbolMetadataKeys.Arity] = type.Arity;
            metadata[SymbolMetadataKeys.BaseType] = type.TypeKind == TypeKind.Interface || type.BaseType == null
                ? null
                : type.BaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata[SymbolMetadataKeys.Interfaces] = type.Interfaces
                .Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        else if (symbol is IPropertySymbol prop)
        {
            metadata[SymbolMetadataKeys.ReturnType] = prop.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata[SymbolMetadataKeys.IsAbstract] = prop.IsAbstract;
            metadata[SymbolMetadataKeys.IsVirtual] = prop.IsVirtual;
            metadata[SymbolMetadataKeys.IsOverride] = prop.IsOverride;
            metadata[SymbolMetadataKeys.IsStatic] = prop.IsStatic;
            metadata[SymbolMetadataKeys.IsReadOnly] = prop.IsReadOnly;
            metadata[SymbolMetadataKeys.IsWriteOnly] = prop.IsWriteOnly;
            metadata[SymbolMetadataKeys.Accessibility] = prop.DeclaredAccessibility.ToString();
            metadata[SymbolMetadataKeys.Signature] = prop.ToDisplayString(SignatureFormat);
        }
        else if (symbol is IFieldSymbol field)
        {
            metadata[SymbolMetadataKeys.ReturnType] = field.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata[SymbolMetadataKeys.IsStatic] = field.IsStatic;
            metadata[SymbolMetadataKeys.IsReadOnly] = field.IsReadOnly;
            metadata[SymbolMetadataKeys.IsConst] = field.IsConst;
            metadata[SymbolMetadataKeys.IsVolatile] = field.IsVolatile;
            metadata[SymbolMetadataKeys.Accessibility] = field.DeclaredAccessibility.ToString();
        }
        else if (symbol is IEventSymbol evt)
        {
            metadata[SymbolMetadataKeys.ReturnType] = evt.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata[SymbolMetadataKeys.IsAbstract] = evt.IsAbstract;
            metadata[SymbolMetadataKeys.IsVirtual] = evt.IsVirtual;
            metadata[SymbolMetadataKeys.IsOverride] = evt.IsOverride;
            metadata[SymbolMetadataKeys.IsStatic] = evt.IsStatic;
            metadata[SymbolMetadataKeys.Accessibility] = evt.DeclaredAccessibility.ToString();
            metadata[SymbolMetadataKeys.Signature] = evt.ToDisplayString(SignatureFormat);
        }

        var attrs = symbol.GetAttributes()
            .Select(AttributeFormatter.FormatAttribute)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (attrs.Count > 0)
            metadata[SymbolMetadataKeys.Attributes] = attrs;

        return metadata.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(metadata)
            : null;
    }

    private static string? DeriveGeneratorIdentity(byte[] content, string encodingName)
    {
        if (content.Length == 0)
            return null;

        var headerLength = Math.Min(512, content.Length);
        var headerText = DeclarationSpanComputer.GetEncoding(encodingName).GetString(content, 0, headerLength);

        // Match GeneratedCode(...) tolerating a namespace-qualified attribute
        // (e.g. [System.CodeDom.Compiler.GeneratedCode(...)]) and arbitrary
        // whitespace around the parenthesis/argument. \b anchors on a word
        // boundary so a type merely ending in "GeneratedCode" is not matched,
        // and the first double-quoted argument is captured as the tool name.
        var generatedCodeMatch = GeneratedCodeToolPattern().Match(headerText);
        if (generatedCodeMatch.Success)
        {
            var toolName = generatedCodeMatch.Groups[1].Value;
            if (!string.IsNullOrEmpty(toolName))
                return toolName;
        }

        if (headerText.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase))
        {
            var autoGenIndex = headerText.IndexOf("<auto-generated>", StringComparison.OrdinalIgnoreCase);
            var afterTag = headerText[(autoGenIndex + "<auto-generated>".Length)..].TrimStart();

            var toolName = new string([.. afterTag.TakeWhile(c => c != '/' && c != '\n' && c != '\r' && c != '>')]).Trim();
            if (!string.IsNullOrEmpty(toolName))
                return toolName;

            return "auto-generated-header";
        }

        return null;
    }
}
