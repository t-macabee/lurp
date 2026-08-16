using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal abstract class ExtractionContextBase
{
    protected readonly string _gitRoot;
    private readonly EdgeLocationResolver _locationResolver;
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache;

    protected ExtractionContextBase(Compilation compilation, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null, BindingIncompletenessCollector? incompleteness = null,
        Dictionary<SyntaxTree, SemanticModel>? semanticModelCache = null, IEnumerable<string>? documentPaths = null, IEnumerable<string>? generatedDocumentPaths = null)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        _gitRoot = gitRoot ?? throw new ArgumentNullException(nameof(gitRoot));
        AssemblyIdentity = compilation.Assembly.Identity.GetDisplayName();
        ScopeDocuments = scopeDocuments;
        Incompleteness = incompleteness;
        _semanticModelCache = semanticModelCache ?? [];
        _locationResolver = new EdgeLocationResolver(documentPaths ?? [], generatedDocumentPaths ?? [], _gitRoot);
    }

    internal Compilation Compilation { get; }
    internal string SnapshotId { get; }
    internal string AssemblyIdentity { get; }
    internal IReadOnlySet<string>? ScopeDocuments { get; }
    internal BindingIncompletenessCollector? Incompleteness { get; }

    /// <summary>
    ///     True when <paramref name="documentPath" /> is a generated document, per the
    ///     shared <see cref="EdgeLocationResolver" /> detection (pre-computed generated
    ///     set + conventional-name heuristics). Emitters set an edge's
    ///     <c>IsCrossGenerated</c> from this so polymorphism/reflection edges carry the
    ///     same generated-source signal the member-edge lineage already records.
    /// </summary>
    internal bool IsGenerated(string? documentPath)
    {
        return _locationResolver.IsGenerated(documentPath);
    }

    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node)
    {
        Incompleteness?.RecordFilteredExternal(resolvedTarget, node, Compilation);
    }

    internal SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree)
    {
        if (_semanticModelCache.TryGetValue(syntaxTree, out var model))
            return model;

        model = Compilation.GetSemanticModel(syntaxTree);
        _semanticModelCache[syntaxTree] = model;
        return model;
    }

    internal string? MakeSymbolId(ISymbol symbol)
    {
        return SymbolIdFactory.Make(symbol, AssemblyIdentity);
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        if (location == null || !location.IsInSource)
            return (null, null, null, null, null);

        var lineSpan = location.GetLineSpan();
        var filePath = location.SourceTree?.FilePath;
        var relativePath = string.IsNullOrEmpty(filePath) ? null : PathNormalizer.ToGitRelative(filePath, _gitRoot);
        return (relativePath,
            lineSpan.StartLinePosition.Line,
            lineSpan.StartLinePosition.Character,
            lineSpan.EndLinePosition.Line,
            lineSpan.EndLinePosition.Character);
    }
}