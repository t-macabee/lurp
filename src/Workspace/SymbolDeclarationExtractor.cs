using Microsoft.CodeAnalysis;
using System.Buffers;
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

    // B24/B3: a C# 14 extension block property is declared on the marker type (the nested
    // extension type), and its accessors there carry an AssociatedExtensionImplementation that
    // names the real accessor on the outer static class. The implementation accessor is an ordinary
    // method there, with no MethodKind and no AssociatedSymbol of its own, so this map (implementation
    // accessor -> block accessor) is how its metadata gets its accessor_kind and associated_symbol.
    // Built once per ExtractAll.
    private readonly Dictionary<IMethodSymbol, IMethodSymbol> _extensionImplementationAccessors = new(SymbolEqualityComparer.Default);

    // B62: every implementation of a block member (methods, static members and accessors).
    // Roslyn marks these implicit, but they are user code, so BuildMetadataJson does not
    // write is_implicitly_declared for them.
    private readonly HashSet<IMethodSymbol> _extensionImplementations = new(SymbolEqualityComparer.Default);

    // D48: members declared in the compilation that implement (implicitly or explicitly)
    // a member of an interface declared outside the compilation. Built once per ExtractAll
    // across every named type in the compilation so full and incremental runs write the same metadata.
    private readonly HashSet<ISymbol> _externalInterfaceImplementations = new(SymbolEqualityComparer.Default);

    // Per-document cache for DeriveGeneratorIdentity so a generated file with many symbols
    // is scanned once per extraction pass.
    private readonly Dictionary<DocumentId, string?> _generatorIdentities = new();

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        SymbolDisplayGlobalNamespaceStyle.Omitted,
        SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeTypeConstraints,
        SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType |
        SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
    );

    private static readonly SearchValues<char> AutoGenToolTerminators = SearchValues.Create("/\n\r><");

    [GeneratedRegex(@"\bGeneratedCode\s*\(\s*""([^""]*)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedCodeToolPattern();

    internal List<SymbolDeclaration> ExtractAll()
    {
        var results = new List<SymbolDeclaration>();

        BuildExtensionImplementationMaps();
        BuildExternalInterfaceImplementations();

        foreach (var typeSymbol in ExtractionUtils.GetNamespaceTypeMembers(context.Compilation.Assembly.GlobalNamespace)) ExtractTypeDeclarations(typeSymbol, results);

        return results;
    }

    private void BuildExternalInterfaceImplementations()
    {
        foreach (var typeSymbol in ExtractionUtils.GetAllNamedTypes(context.Compilation.Assembly.GlobalNamespace))
        {
            foreach (var iface in typeSymbol.AllInterfaces)
            {
                if (!ExtractionUtils.IsOutsideCompilation(iface.OriginalDefinition, context.Compilation))
                    continue;

                foreach (var member in iface.GetMembers())
                {
                    var implementation = typeSymbol.FindImplementationForInterfaceMember(member);
                    if (implementation != null && !ExtractionUtils.IsOutsideCompilation(implementation.OriginalDefinition, context.Compilation))
                        _externalInterfaceImplementations.Add(implementation.OriginalDefinition);
                }
            }
        }
    }

    private void BuildExtensionImplementationMaps()
    {
        foreach (var typeSymbol in ExtractionUtils.GetAllNamedTypes(context.Compilation.Assembly.GlobalNamespace))
        {
            if (!typeSymbol.IsExtension)
                continue;

            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is IMethodSymbol { AssociatedExtensionImplementation: IMethodSymbol methodImplementation })
                    _extensionImplementations.Add(methodImplementation);

                if (member is not IPropertySymbol property)
                    continue;

                foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
                {
                    if (accessor == null)
                        continue;

                    if (accessor.AssociatedExtensionImplementation is IMethodSymbol implementation)
                    {
                        _extensionImplementationAccessors[implementation] = accessor;
                    }
                }
            }
        }
    }

    private void ExtractTypeDeclarations(INamedTypeSymbol typeSymbol, List<SymbolDeclaration> results)
    {
        AddSymbolDeclarations(typeSymbol, results);

        foreach (var nestedType in typeSymbol.GetTypeMembers()) ExtractTypeDeclarations(nestedType, results);

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is INamedTypeSymbol)
                continue;

            // B3 step 2a: a C# 14 extension block declares a block member and its
            // implementation on the outer static class. Only the implementation is
            // canonical, so the block method symbol is not declared.
            if (member is IMethodSymbol { ContainingType.IsExtension: true })
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
        var metadataJson = BuildMetadataJson(symbol, context.Compilation, _entryPointMethod, _extensionImplementationAccessors, _extensionImplementations, _externalInterfaceImplementations);

        var symbolId = new SymbolId(docCommentId, context.AssemblyIdentity, fqn);
        var isPartial = symbol is INamedTypeSymbol { DeclaringSyntaxReferences.Length: > 1 };

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

            var (fullSpan, signatureSpan, bodySpan, nameSpan) = DeclarationSpanComputer.ComputeSpans(
                EdgeLocationResolver.DeclarationNode(symbol, syntaxNode), sourceString, encoding);

            var isGenerated = context.GeneratedDocuments.Contains(documentId.Value);
            string? generatorIdentity = null;
            if (isGenerated)
            {
                if (!_generatorIdentities.TryGetValue(documentId.Value, out generatorIdentity))
                {
                    generatorIdentity = DeriveGeneratorIdentity(syntaxTree, sourceString);
                    _generatorIdentities[documentId.Value] = generatorIdentity;
                }
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
                TargetFramework = context.TargetFramework
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

    private static string? BuildMetadataJson(
        ISymbol symbol,
        Compilation compilation,
        IMethodSymbol? entryPointMethod,
        Dictionary<IMethodSymbol, IMethodSymbol> extensionImplementationAccessors,
        HashSet<IMethodSymbol> extensionImplementations,
        HashSet<ISymbol> externalInterfaceImplementations)
    {
        var metadata = new Dictionary<string, object?>();

        // B62: the implementation of a block member is user code. Roslyn marks it implicit only because
        // the compiler emits it on the outer class, so it is not written as implicitly declared.
        if (symbol.IsImplicitlyDeclared && !(symbol is IMethodSymbol implementation && extensionImplementations.Contains(implementation)))
            metadata[SymbolMetadataKeys.IsImplicitlyDeclared] = true;

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
                metadata[SymbolMetadataKeys.EffectiveAccessibility] = ComputeEffectiveAccessibility(method).ToString();
                metadata[SymbolMetadataKeys.Arity] = method.Arity;
                metadata[SymbolMetadataKeys.IsExtensionMethod] = method.IsExtensionMethod;
                metadata[SymbolMetadataKeys.Signature] = method.ToDisplayString(SignatureFormat);
                if (entryPointMethod != null && SymbolEqualityComparer.Default.Equals(method, entryPointMethod))
                    metadata[SymbolMetadataKeys.IsEntryPoint] = true;
                if (method.MethodKind == MethodKind.StaticConstructor)
                    metadata[SymbolMetadataKeys.IsStaticConstructor] = true;
                // B24: an implementation accessor takes its kind and its property from the block accessor.
                var blockAccessor = extensionImplementationAccessors.GetValueOrDefault(method);
                var accessorKind = AccessorKindOf(blockAccessor ?? method);
                if (accessorKind != null)
                {
                    metadata[SymbolMetadataKeys.AccessorKind] = accessorKind;
                    var associatedSymbol = blockAccessor?.AssociatedSymbol ?? method.AssociatedSymbol;
                    var associatedId = associatedSymbol?.GetDocumentationCommentId();
                    if (!string.IsNullOrEmpty(associatedId))
                        metadata[SymbolMetadataKeys.AssociatedSymbol] = associatedId;
                }
                if (ImplementsExternalMember(compilation, method, externalInterfaceImplementations))
                    metadata[SymbolMetadataKeys.ImplementsExternalMember] = true;
                break;
            case INamedTypeSymbol type:
                metadata[SymbolMetadataKeys.TypeKind] = type.TypeKind.ToString();
                metadata[SymbolMetadataKeys.IsAbstract] = type.IsAbstract;
                metadata[SymbolMetadataKeys.IsStatic] = type.IsStatic;
                metadata[SymbolMetadataKeys.IsRecord] = type.IsRecord;
                metadata[SymbolMetadataKeys.Accessibility] = type.DeclaredAccessibility.ToString();
                metadata[SymbolMetadataKeys.EffectiveAccessibility] = ComputeEffectiveAccessibility(type).ToString();
                metadata[SymbolMetadataKeys.Arity] = type.Arity;
                metadata[SymbolMetadataKeys.BaseType] = type.TypeKind == TypeKind.Interface || type.BaseType == null
                    ? null
                    : type.BaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                metadata[SymbolMetadataKeys.Interfaces] = type.Interfaces
                    .Select(i => i.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                if (type.IsExtension)
                    metadata[SymbolMetadataKeys.IsExtensionBlock] = true;
                if (entryPointMethod?.ContainingType != null && SymbolEqualityComparer.Default.Equals(type, entryPointMethod.ContainingType))
                    metadata[SymbolMetadataKeys.ContainsEntryPoint] = true;
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
                metadata[SymbolMetadataKeys.EffectiveAccessibility] = ComputeEffectiveAccessibility(prop).ToString();
                metadata[SymbolMetadataKeys.Signature] = prop.ToDisplayString(SignatureFormat);
                if (ImplementsExternalMember(compilation, prop, externalInterfaceImplementations))
                    metadata[SymbolMetadataKeys.ImplementsExternalMember] = true;
                break;
            case IFieldSymbol field:
                metadata[SymbolMetadataKeys.ReturnType] = field.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                metadata[SymbolMetadataKeys.IsStatic] = field.IsStatic;
                metadata[SymbolMetadataKeys.IsReadOnly] = field.IsReadOnly;
                metadata[SymbolMetadataKeys.IsConst] = field.IsConst;
                metadata[SymbolMetadataKeys.IsVolatile] = field.IsVolatile;
                metadata[SymbolMetadataKeys.Accessibility] = field.DeclaredAccessibility.ToString();
                metadata[SymbolMetadataKeys.EffectiveAccessibility] = ComputeEffectiveAccessibility(field).ToString();
                break;
            case IEventSymbol evt:
                metadata[SymbolMetadataKeys.ReturnType] = evt.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                metadata[SymbolMetadataKeys.IsAbstract] = evt.IsAbstract;
                metadata[SymbolMetadataKeys.IsVirtual] = evt.IsVirtual;
                metadata[SymbolMetadataKeys.IsOverride] = evt.IsOverride;
                metadata[SymbolMetadataKeys.IsStatic] = evt.IsStatic;
                metadata[SymbolMetadataKeys.Accessibility] = evt.DeclaredAccessibility.ToString();
                metadata[SymbolMetadataKeys.EffectiveAccessibility] = ComputeEffectiveAccessibility(evt).ToString();
                metadata[SymbolMetadataKeys.Signature] = evt.ToDisplayString(SignatureFormat);
                if (ImplementsExternalMember(compilation, evt, externalInterfaceImplementations))
                    metadata[SymbolMetadataKeys.ImplementsExternalMember] = true;
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

    /// <summary>
    ///     D168.4 / C# §7.5.3: the meet of a symbol's declared accessibility with the declared
    ///     accessibility of every containing type up the chain.
    /// </summary>
    private static Accessibility ComputeEffectiveAccessibility(ISymbol symbol)
    {
        var effective = symbol.DeclaredAccessibility;
        for (var container = symbol.ContainingType; container != null; container = container.ContainingType)
            effective = MeetAccessibility(effective, container.DeclaredAccessibility);
        return effective;
    }

    private static Accessibility MeetAccessibility(Accessibility a, Accessibility b)
    {
        if (a == b) return a;
        if (a == Accessibility.Private || b == Accessibility.Private) return Accessibility.Private;
        if (a == Accessibility.NotApplicable) return b;
        if (b == Accessibility.NotApplicable) return a;
        if (a == Accessibility.Public) return b;
        if (b == Accessibility.Public) return a;
        if (a == Accessibility.ProtectedAndInternal || b == Accessibility.ProtectedAndInternal) return Accessibility.ProtectedAndInternal;
        if (a == Accessibility.ProtectedOrInternal) return b;
        if (b == Accessibility.ProtectedOrInternal) return a;
        // Remaining pair: Internal and Protected.
        return Accessibility.ProtectedAndInternal;
    }

    private static string? AccessorKindOf(IMethodSymbol method)
    {
        return method.MethodKind switch
        {
            MethodKind.PropertyGet => "get",
            MethodKind.PropertySet => method.IsInitOnly ? "init" : "set",
            MethodKind.EventAdd => "add",
            MethodKind.EventRemove => "remove",
            MethodKind.EventRaise => "raise",
            _ => null
        };
    }

    private static bool ImplementsExternalMember(Compilation compilation, ISymbol member, HashSet<ISymbol> externalInterfaceImplementations)
    {
        if (externalInterfaceImplementations.Contains(member.OriginalDefinition))
            return true;

        IEnumerable<ISymbol> implementations = member switch
        {
            IMethodSymbol method => method.ExplicitInterfaceImplementations.AsEnumerable(),
            IPropertySymbol property => property.ExplicitInterfaceImplementations.AsEnumerable(),
            IEventSymbol @event => @event.ExplicitInterfaceImplementations.AsEnumerable(),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member.Kind,
                "Only a method, property or event can implement an interface member.")
        };

        foreach (var implemented in implementations)
        {
            if (ExtractionUtils.IsOutsideCompilation(implemented, compilation))
                return true;
        }

        for (var overridden = ExtractionUtils.OverriddenMember(member);
             overridden != null;
             overridden = ExtractionUtils.OverriddenMember(overridden))
        {
            if (ExtractionUtils.IsOutsideCompilation(overridden, compilation))
                return true;
        }

        return false;
    }

    private static string? DeriveGeneratorIdentity(SyntaxTree syntaxTree, string sourceText)
    {
        if (sourceText.Length == 0)
            return null;

        // Match GeneratedCode(...) tolerating a namespace-qualified attribute
        // (e.g. [System.CodeDom.Compiler.GeneratedCode(...)]) and arbitrary
        // whitespace around the parenthesis/argument. \b anchors on a word
        // boundary so a type merely ending in "GeneratedCode" is not matched,
        // and the first double-quoted argument is captured as the tool name.
        var generatedCodeMatch = GeneratedCodeToolPattern().Match(sourceText);
        if (generatedCodeMatch.Success)
        {
            var toolName = generatedCodeMatch.Groups[1].Value;
            if (!string.IsNullOrEmpty(toolName))
                return toolName;
        }

        // Use the same ordinal leading-comment match (with no byte cap, matching
        // both <auto-generated and <autogenerated) as GeneratedCode.
        var tail = GeneratedCode.TryGetAutoGeneratedCommentTail(syntaxTree.GetRoot().GetLeadingTrivia());
        if (tail == null)
            return null;

        var span = tail.AsSpan().TrimStart(" \t");
        if (span.StartsWith("/>", StringComparison.Ordinal))
            span = span[2..];
        else if (span.StartsWith(">", StringComparison.Ordinal))
            span = span[1..];

        var afterTag = span.TrimStart(" \t");
        var end = afterTag.IndexOfAny(AutoGenToolTerminators);
        var toolSpan = (end >= 0 ? afterTag[..end] : afterTag).Trim();
        if (!toolSpan.IsEmpty)
            return toolSpan.ToString();

        return "auto-generated-header";
    }
}