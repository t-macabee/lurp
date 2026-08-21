using Microsoft.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using SymKind = Lurp.Storage.IndexedSymbolKind;

namespace Lurp.Workspace;

internal sealed partial class SymbolDeclarationExtractor(SymbolExtractionContext context)
{
    // Computed once per compilation. GetEntryPoint returns the Main method (explicit or the
    // compiler-synthesized top-level-statements form) for an executable project, null for a
    // library. Neither form sets IsImplicitlyDeclared and both carry an ordinary-looking
    // docCommentId (e.g. top-level statements: "M:Program.{Main}$(System.String[])", containing
    // type "T:Program" — structurally indistinguishable from a hand-written Program.Main), so
    // identity comparison against this cached symbol is the only reliable way to recognize it;
    // name/pattern matching on the FQN is not (observed FQN rendering already differs across
    // SDKs: "Program.<top-level-statements-entry-point>" vs. an invalid-code fallback like
    // "<invalid-global-code>" when the source has unrelated syntax errors).
    private readonly IMethodSymbol? _entryPointMethod = context.Compilation.GetEntryPoint(CancellationToken.None);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        SymbolDisplayGlobalNamespaceStyle.Omitted,
        SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
        SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    [GeneratedRegex(@"\bGeneratedCode\s*\(\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedCodeToolPattern();

    internal List<SymbolDeclaration> ExtractAll()
    {
        var results = new List<SymbolDeclaration>();

        foreach (var typeSymbol in ExtractionUtils.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace)) ExtractTypeDeclarations(typeSymbol, results);

        return results;
    }

    private void ExtractTypeDeclarations(INamedTypeSymbol typeSymbol, List<SymbolDeclaration> results)
    {
        AddSymbolDeclarations(typeSymbol, results);

        foreach (var nestedType in typeSymbol.GetTypeMembers()) ExtractTypeDeclarations(nestedType, results);

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
        var metadataJson = BuildMetadataJson(symbol, _entryPointMethod);

        var symbolId = new SymbolId(docCommentId, context.AssemblyIdentity, fqn);
        var isPartial = symbol is INamedTypeSymbol { DeclaringSyntaxReferences.Length: > 1 } typeSymbol;

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
            if (isGenerated && context.DocumentContents.TryGetValue(documentId.Value, out var genDocContent)) generatorIdentity = DeriveGeneratorIdentity(genDocContent.Content, genDocContent.Encoding);

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
                GeneratorIdentity = generatorIdentity
            });
        }
    }

    private static string BuildFullyQualifiedName(ISymbol symbol)
    {
        var name = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        if (symbol is INamedTypeSymbol || symbol.ContainingType == null)
            return name;

        var typeFqn = symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return $"{typeFqn}.{name}";
    }

    private static SymKind MapKind(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.Namespace => SymKind.Namespace,
            SymbolKind.NamedType => SymKind.Type,
            SymbolKind.Method => SymKind.Method,
            SymbolKind.Property => SymKind.Property,
            SymbolKind.Field => SymKind.Field,
            SymbolKind.Event => SymKind.Event,
            SymbolKind.Parameter => SymKind.Parameter,
            SymbolKind.Local => SymKind.Local,
            SymbolKind.RangeVariable => SymKind.RangeVariable,
            SymbolKind.ArrayType => SymKind.ArrayType,
            SymbolKind.PointerType => SymKind.PointerType,
            SymbolKind.TypeParameter => SymKind.TypeParameter,
            _ => SymKind.Unknown
        };
    }

    private static string? BuildMetadataJson(ISymbol symbol, IMethodSymbol? entryPointMethod)
    {
        var metadata = new Dictionary<string, object?>();

        switch (symbol)
        {
            case IMethodSymbol method:
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
                if (entryPointMethod != null && SymbolEqualityComparer.Default.Equals(method, entryPointMethod))
                    metadata[SymbolMetadataKeys.IsEntryPoint] = true;
                break;
            case INamedTypeSymbol type:
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
                break;
            case IPropertySymbol prop:
                metadata[SymbolMetadataKeys.ReturnType] = prop.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                metadata[SymbolMetadataKeys.IsAbstract] = prop.IsAbstract;
                metadata[SymbolMetadataKeys.IsVirtual] = prop.IsVirtual;
                metadata[SymbolMetadataKeys.IsOverride] = prop.IsOverride;
                metadata[SymbolMetadataKeys.IsStatic] = prop.IsStatic;
                metadata[SymbolMetadataKeys.IsReadOnly] = prop.IsReadOnly;
                metadata[SymbolMetadataKeys.IsWriteOnly] = prop.IsWriteOnly;
                metadata[SymbolMetadataKeys.Accessibility] = prop.DeclaredAccessibility.ToString();
                metadata[SymbolMetadataKeys.Signature] = prop.ToDisplayString(SignatureFormat);
                break;
            case IFieldSymbol field:
                metadata[SymbolMetadataKeys.ReturnType] = field.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                metadata[SymbolMetadataKeys.IsStatic] = field.IsStatic;
                metadata[SymbolMetadataKeys.IsReadOnly] = field.IsReadOnly;
                metadata[SymbolMetadataKeys.IsConst] = field.IsConst;
                metadata[SymbolMetadataKeys.IsVolatile] = field.IsVolatile;
                metadata[SymbolMetadataKeys.Accessibility] = field.DeclaredAccessibility.ToString();
                break;
            case IEventSymbol evt:
                metadata[SymbolMetadataKeys.ReturnType] = evt.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                metadata[SymbolMetadataKeys.IsAbstract] = evt.IsAbstract;
                metadata[SymbolMetadataKeys.IsVirtual] = evt.IsVirtual;
                metadata[SymbolMetadataKeys.IsOverride] = evt.IsOverride;
                metadata[SymbolMetadataKeys.IsStatic] = evt.IsStatic;
                metadata[SymbolMetadataKeys.Accessibility] = evt.DeclaredAccessibility.ToString();
                metadata[SymbolMetadataKeys.Signature] = evt.ToDisplayString(SignatureFormat);
                break;
        }

        var attrs = symbol.GetAttributes()
            .Select(AttributeFormatter.FormatAttribute)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (attrs.Count > 0)
            metadata[SymbolMetadataKeys.Attributes] = attrs;

        return metadata.Count > 0
            ? JsonSerializer.Serialize(metadata)
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

        if (!headerText.Contains("<auto-generated>", StringComparison.OrdinalIgnoreCase))
            return null;

        var autoGenIndex = headerText.IndexOf("<auto-generated>", StringComparison.OrdinalIgnoreCase);
        var afterTag = headerText[(autoGenIndex + "<auto-generated>".Length)..].TrimStart();

        var autoGenToolName = new string([.. afterTag.TakeWhile(c => c != '/' && c != '\n' && c != '\r' && c != '>')]).Trim();
        if (!string.IsNullOrEmpty(autoGenToolName))
            return autoGenToolName;

        return "auto-generated-header";
    }
}