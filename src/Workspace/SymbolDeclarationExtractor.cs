using System.Text;
using Microsoft.CodeAnalysis;
using Lurp.Storage;
using SymKind = Lurp.Storage.IndexedSymbolKind;

#if CODE_ANALYSIS
using System.Diagnostics.CodeAnalysis;
#endif

namespace Lurp.Workspace;

#if CODE_ANALYSIS
[SuppressMessage("NDepend", "ND1000", Justification = "Handles Roslyn's full symbol-kind surface for declaration extraction; span/attribute helpers were already extracted per remediation task 11 (450 -> 266 lines). Remaining size mirrors Roslyn's API surface (one dispatch branch per symbol kind), not scattered concerns. Review trigger: re-evaluate if a symbol kind's handling grows its own nested branching, or if size regresses back toward the pre-task-11 baseline.")]
#endif
internal sealed class SymbolDeclarationExtractor(SymbolExtractionContext context, Action<string>? logWarning = null)
{
    private readonly Action<string>? _logWarning = logWarning;

    internal List<SymbolDeclaration> ExtractAll()
    {
        var results = new List<SymbolDeclaration>();

        foreach (var typeSymbol in SymbolExtractionContext.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace))
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
            metadata["returnType"] = method.ReturnType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isAbstract"] = method.IsAbstract;
            metadata["isVirtual"] = method.IsVirtual;
            metadata["isOverride"] = method.IsOverride;
            metadata["isStatic"] = method.IsStatic;
            metadata["isAsync"] = method.IsAsync;
            metadata["accessibility"] = method.DeclaredAccessibility.ToString();
            metadata["arity"] = method.Arity;
            metadata["isExtensionMethod"] = method.IsExtensionMethod;
            metadata["signature"] = method.ToDisplayString(SignatureFormat);
        }
        else if (symbol is INamedTypeSymbol type)
        {
            metadata["typeKind"] = type.TypeKind.ToString();
            metadata["isAbstract"] = type.IsAbstract;
            metadata["isStatic"] = type.IsStatic;
            metadata["isRecord"] = type.IsRecord;
            metadata["accessibility"] = type.DeclaredAccessibility.ToString();
            metadata["arity"] = type.Arity;
            metadata["base_type"] = type.TypeKind == TypeKind.Interface || type.BaseType == null
                ? null
                : type.BaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["interfaces"] = type.Interfaces
                .Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        else if (symbol is IPropertySymbol prop)
        {
            metadata["returnType"] = prop.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isAbstract"] = prop.IsAbstract;
            metadata["isVirtual"] = prop.IsVirtual;
            metadata["isOverride"] = prop.IsOverride;
            metadata["isStatic"] = prop.IsStatic;
            metadata["isReadOnly"] = prop.IsReadOnly;
            metadata["isWriteOnly"] = prop.IsWriteOnly;
            metadata["accessibility"] = prop.DeclaredAccessibility.ToString();
            metadata["signature"] = prop.ToDisplayString(SignatureFormat);
        }
        else if (symbol is IFieldSymbol field)
        {
            metadata["returnType"] = field.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isStatic"] = field.IsStatic;
            metadata["isReadOnly"] = field.IsReadOnly;
            metadata["isConst"] = field.IsConst;
            metadata["isVolatile"] = field.IsVolatile;
            metadata["accessibility"] = field.DeclaredAccessibility.ToString();
        }
        else if (symbol is IEventSymbol evt)
        {
            metadata["returnType"] = evt.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            metadata["isAbstract"] = evt.IsAbstract;
            metadata["isVirtual"] = evt.IsVirtual;
            metadata["isOverride"] = evt.IsOverride;
            metadata["isStatic"] = evt.IsStatic;
            metadata["accessibility"] = evt.DeclaredAccessibility.ToString();
            metadata["signature"] = evt.ToDisplayString(SignatureFormat);
        }

        var attrs = symbol.GetAttributes()
            .Select(AttributeFormatter.FormatAttribute)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (attrs.Count > 0)
            metadata["attributes"] = attrs;

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

        var generatedCodeAttr = "[GeneratedCode(";
        var attrIndex = headerText.IndexOf(generatedCodeAttr, StringComparison.OrdinalIgnoreCase);
        if (attrIndex >= 0)
        {
            var start = attrIndex + generatedCodeAttr.Length;
            var end = headerText.IndexOf('"', start + 1);
            if (end > start)
            {
                return headerText[(start + 1)..end];
            }
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