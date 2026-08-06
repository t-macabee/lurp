using Lurp.Shared;
using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Workspace;

internal sealed class MemberEdgeExtractionContext(Compilation compilation, IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions, IReadOnlySet<DocumentId> generatedDocuments, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null, Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null)
{
    private readonly string _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    private readonly EdgeLocationResolver _locationResolver = new(
        documentVersions.Keys.Select(static id => id.ToString()),
        generatedDocuments.Select(static id => id.ToString()),
        gitRoot);
    private List<(IMethodSymbol Method, CSharpSyntaxNode Syntax)>? _methodDeclarations;
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache = semanticModelCache ?? [];

    internal Compilation Compilation { get; } = compilation ?? throw new ArgumentNullException(nameof(compilation));
    internal EdgeLocationResolver LocationResolver => _locationResolver;
    internal string SnapshotId { get; } = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
    internal IReadOnlySet<string>? ScopeDocuments { get; } = scopeDocuments;
    internal BindingIncompletenessCollector? Incompleteness { get; } = incompleteness;

    internal void RecordUnresolvedBinding(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
        => Incompleteness?.RecordUnresolved(symbolInfo, node, semanticModel);

    internal void RecordUnresolvedBinding(SyntaxNode node, SemanticModel semanticModel)
        => Incompleteness?.RecordUnresolved(node, semanticModel);

    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node)
        => Incompleteness?.RecordFilteredExternal(resolvedTarget, node, Compilation);

    private bool IsSyntaxTreeInScope(SyntaxTree? syntaxTree)
    {
        if (ScopeDocuments == null || syntaxTree == null)
            return true;
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return true;
        return ScopeDocuments.Contains(filePath.Replace('\\', '/'));
    }

    internal bool IsMemberInScope(ISymbol member)
    {
        if (ScopeDocuments == null)
            return true;

        var syntaxRefs = member.DeclaringSyntaxReferences;
        if (syntaxRefs.IsEmpty)
        {
            // Compiler-synthesized members (implicit constructors, auto-property
            // backing fields) have no declaring syntax of their own : fall back to
            // the containing type's scope.
            var containingType = member.ContainingType;
            if (containingType == null)
                return true;
            foreach (var syntaxRef in containingType.DeclaringSyntaxReferences)
            {
                if (IsSyntaxTreeInScope(syntaxRef.SyntaxTree))
                    return true;
            }
            return false;
        }

        foreach (var syntaxRef in syntaxRefs)
        {
            if (IsSyntaxTreeInScope(syntaxRef.SyntaxTree))
                return true;
        }
        return false;
    }

    internal IEnumerable<INamedTypeSymbol> GetAllNamedTypes() => GetNamespaceTypeMembers(Compilation.Assembly.GlobalNamespace);

    internal static SyntaxNode? GetMethodBody(CSharpSyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax m => m.Body ?? (SyntaxNode?)m.ExpressionBody,
            ConstructorDeclarationSyntax c => c.Body ?? (SyntaxNode?)c.ExpressionBody,
            AccessorDeclarationSyntax a => a.Body ?? (SyntaxNode?)a.ExpressionBody,
            _ => null
        };
    }

    internal IReadOnlyList<(IMethodSymbol Method, CSharpSyntaxNode Syntax)> EnumerateMethodDeclarations()
    {
        if (_methodDeclarations != null)
            return _methodDeclarations;
        var result = new List<(IMethodSymbol Method, CSharpSyntaxNode Syntax)>();
        foreach (var typeSymbol in GetAllNamedTypes())
        {
            foreach (var member in typeSymbol.GetMembers())
            {
                if (member is IMethodSymbol method)
                {
                    foreach (var syntaxRef in method.DeclaringSyntaxReferences)
                    {
                        if (!IsSyntaxTreeInScope(syntaxRef.SyntaxTree))
                            continue;
                        var syntax = syntaxRef.GetSyntax();
                        if (syntax is MethodDeclarationSyntax methodSyntax)
                            result.Add((method, methodSyntax));
                        else if (syntax is ConstructorDeclarationSyntax ctorSyntax)
                            result.Add((method, ctorSyntax));
                    }
                }

                if (member is IPropertySymbol property)
                {
                    foreach (var accessor in new[] { property.GetMethod, property.SetMethod })
                    {
                        if (accessor == null)
                            continue;

                        foreach (var syntaxRef in accessor.DeclaringSyntaxReferences)
                        {
                            if (!IsSyntaxTreeInScope(syntaxRef.SyntaxTree))
                                continue;
                            if (syntaxRef.GetSyntax() is AccessorDeclarationSyntax accessorSyntax)
                                result.Add((accessor, accessorSyntax));
                        }
                    }
                }
            }
        }
        _methodDeclarations = result;
        return result;
    }


    internal string? MakeSymbolId(ISymbol symbol)
    {
        return SymbolIdFactory.Make(symbol, _assemblyIdentity);
    }

    internal EdgeRecord MakeEdge(string sourceId, string targetId, string kind, string extractorVersion, (string? path, int? sl, int? sc, int? el, int? ec)? location)
    {
        var sourceDocumentPath = location?.path;
        var isSourceGenerated = IsGeneratedDocument(sourceDocumentPath);

        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = Provenance.CompilerProved,
            SnapshotId = SnapshotId,
            ExtractorVersion = extractorVersion,
            SourceDocumentPath = sourceDocumentPath,
            SourceStartLine = location?.sl,
            SourceStartColumn = location?.sc,
            SourceEndLine = location?.el,
            SourceEndColumn = location?.ec,
            IsCrossGenerated = isSourceGenerated,
        };
    }

    private bool IsGeneratedDocument(string? documentPath)
        => _locationResolver.IsGenerated(documentPath);

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetMemberSourceLocation(ISymbol member)
    {
        var result = _locationResolver.Resolve(member);
        if (result.path == null && result.sl == null)
            return null;
        return result;
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
        => _locationResolver.Resolve(location);



    internal SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree)
    {
        if (!_semanticModelCache.TryGetValue(syntaxTree, out var model))
        {
            model = Compilation.GetSemanticModel(syntaxTree);
            _semanticModelCache[syntaxTree] = model;
        }
        return model;
    }

    internal SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree, Dictionary<SyntaxTree, SemanticModel> cache)
    {
        if (!cache.TryGetValue(syntaxTree, out var model))
        {
            model = Compilation.GetSemanticModel(syntaxTree);
            cache[syntaxTree] = model;
        }
        return model;
    }

    private static IEnumerable<INamedTypeSymbol> GetNamespaceTypeMembers(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            foreach (var type in GetNamespaceTypeMembers(childNs))
            {
                yield return type;
            }
        }
    }
}

internal interface IMemberEdgeExtractor
{
    List<EdgeRecord> Extract();
}
