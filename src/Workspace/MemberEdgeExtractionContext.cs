using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Workspace;

internal sealed class MemberEdgeExtractionContext(
    Compilation compilation,
    IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
    IReadOnlySet<DocumentId> generatedDocuments,
    string snapshotId,
    string gitRoot,
    IReadOnlySet<string>? scopeDocuments = null,
    BindingIncompletenessCollector? incompleteness = null,
    Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null)
{
    private readonly string _assemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache = semanticModelCache ?? [];
    private List<(IMethodSymbol Method, CSharpSyntaxNode Syntax)>? _methodDeclarations;

    internal Compilation Compilation { get; } = compilation ?? throw new ArgumentNullException(nameof(compilation));

    internal EdgeLocationResolver LocationResolver { get; } = new(
        documentVersions.Keys.Select(static id => id.ToString()),
        generatedDocuments.Select(static id => id.ToString()),
        gitRoot);

    internal string SnapshotId { get; } = snapshotId ?? throw new ArgumentNullException(nameof(snapshotId));
    internal IReadOnlySet<string>? ScopeDocuments { get; } = scopeDocuments;
    internal BindingIncompletenessCollector? Incompleteness { get; } = incompleteness;

    internal void RecordUnresolvedBinding(SymbolInfo symbolInfo, SyntaxNode node, SemanticModel semanticModel)
    {
        Incompleteness?.RecordUnresolved(symbolInfo, node, semanticModel);
    }

    internal void RecordUnresolvedBinding(SyntaxNode node, SemanticModel semanticModel)
    {
        Incompleteness?.RecordUnresolved(node, semanticModel);
    }

    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node)
    {
        Incompleteness?.RecordFilteredExternal(resolvedTarget, node, Compilation);
    }

    private bool IsSyntaxTreeInScope(SyntaxTree? syntaxTree)
    {
        if (ScopeDocuments == null || syntaxTree == null)
            return true;
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return true;
        return ScopeDocuments.Contains(PathNormalizer.ToForwardSlash(filePath));
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
            return containingType.DeclaringSyntaxReferences.Any(syntaxRef => IsSyntaxTreeInScope(syntaxRef.SyntaxTree));
        }

        return syntaxRefs.Any(syntaxRef => IsSyntaxTreeInScope(syntaxRef.SyntaxTree));
    }

    internal IEnumerable<INamedTypeSymbol> GetAllNamedTypes()
    {
        return ExtractionUtils.GetNamespaceTypeMembers(Compilation.Assembly.GlobalNamespace);
    }

    internal static SyntaxNode? GetMethodBody(CSharpSyntaxNode node)
    {
        return ExtractionUtils.GetMethodBody(node);
    }

    internal IReadOnlyList<(IMethodSymbol Method, CSharpSyntaxNode Syntax)> EnumerateMethodDeclarations()
    {
        if (_methodDeclarations != null)
            return _methodDeclarations;
        var result = ExtractionUtils.EnumerateMethodDeclarations(GetAllNamedTypes(), IsSyntaxTreeInScope).ToList();
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
            IsCrossGenerated = isSourceGenerated
        };
    }

    private bool IsGeneratedDocument(string? documentPath)
    {
        return LocationResolver.IsGenerated(documentPath);
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)?
        GetMemberSourceLocation(ISymbol member)
    {
        var result = LocationResolver.Resolve(member);
        if (result.path == null && result.sl == null)
            return null;
        return result;
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        return LocationResolver.Resolve(location);
    }


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
}

internal interface IMemberEdgeExtractor
{
    List<EdgeRecord> Extract();
}