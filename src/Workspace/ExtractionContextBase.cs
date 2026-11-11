using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

/// <summary>
///     The inputs that every extraction stage of one project shares: one semantic-model
///     cache, one <see cref="EdgeLocationResolver" /> and one binding-incompleteness
///     collector for each project (S01-F32).
/// </summary>
internal sealed record ProjectExtractionInputs(
    Compilation Compilation,
    string SnapshotId,
    EdgeLocationResolver LocationResolver,
    IReadOnlySet<string>? ScopeDocuments,
    BindingIncompletenessCollector? Incompleteness,
    Dictionary<SyntaxTree, SemanticModel> SemanticModelCache);

/// <summary>The members that every workspace extraction context shares.</summary>
internal abstract class ExtractionContextBase(ProjectExtractionInputs inputs)
{
    internal Compilation Compilation { get; } = inputs.Compilation;
    internal string SnapshotId { get; } = inputs.SnapshotId;
    internal string AssemblyIdentity { get; } = inputs.Compilation.Assembly.Identity.GetDisplayName();
    internal IReadOnlySet<string>? ScopeDocuments { get; } = inputs.ScopeDocuments;
    internal BindingIncompletenessCollector? Incompleteness { get; } = inputs.Incompleteness;
    internal EdgeLocationResolver LocationResolver { get; } = inputs.LocationResolver;

    /// <summary>
    ///     True when <paramref name="documentPath" /> is a generated document, per the
    ///     shared <see cref="EdgeLocationResolver" /> detection (pre-computed generated
    ///     set + conventional-name heuristics).
    /// </summary>
    internal bool IsGenerated(string? documentPath)
    {
        return LocationResolver.IsGenerated(documentPath);
    }

    internal bool IsInScope(SyntaxTree? syntaxTree)
    {
        return ExtractionUtils.IsInScope(ScopeDocuments, syntaxTree);
    }

    internal IEnumerable<INamedTypeSymbol> GetAllNamedTypes()
    {
        return ExtractionUtils.GetAllNamedTypes(Compilation.Assembly.GlobalNamespace);
    }

    internal (string? path, int? sl, int? sc, int? el, int? ec) Resolve(ISymbol symbol)
    {
        return LocationResolver.Resolve(symbol);
    }

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

    internal SemanticModel GetOrCreateSemanticModel(SyntaxTree syntaxTree)
    {
        if (inputs.SemanticModelCache.TryGetValue(syntaxTree, out var model))
            return model;

        model = Compilation.GetSemanticModel(syntaxTree);
        inputs.SemanticModelCache[syntaxTree] = model;
        return model;
    }

    internal string? MakeSymbolId(ISymbol symbol)
    {
        return SymbolIdFactory.Make(symbol, AssemblyIdentity);
    }

    internal (string? path, int? startLine, int? startColumn, int? endLine, int? endColumn)
        GetLocationInfo(Location location)
    {
        return LocationResolver.Resolve(location);
    }

    /// <summary>The one edge maker of the workspace extractors (S01-F32).</summary>
    internal EdgeRecord MakeEdge(
        string sourceId,
        string targetId,
        string kind,
        string extractorVersion,
        (string? path, int? sl, int? sc, int? el, int? ec)? location,
        string provenance = Provenance.CompilerProved,
        string? typeArgumentsJson = null)
    {
        var sourceDocumentPath = location?.path;
        return new EdgeRecord
        {
            SourceSymbolId = sourceId,
            TargetSymbolId = targetId,
            Kind = kind,
            Provenance = provenance,
            SnapshotId = SnapshotId,
            ExtractorVersion = extractorVersion,
            SourceDocumentPath = sourceDocumentPath,
            SourceStartLine = location?.sl,
            SourceStartColumn = location?.sc,
            SourceEndLine = location?.el,
            SourceEndColumn = location?.ec,
            TypeArgumentsJson = typeArgumentsJson,
            IsCrossGenerated = IsGenerated(sourceDocumentPath)
        };
    }
}
