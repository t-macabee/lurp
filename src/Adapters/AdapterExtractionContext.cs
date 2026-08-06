using Microsoft.CodeAnalysis;
using Lurp.Shared;
using Lurp.Workspace;

namespace Lurp.Adapters;

public sealed class AdapterExtractionContext
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache;

    internal AdapterExtractionContext(
        Compilation compilation,
        string snapshotId,
        EdgeLocationResolver locationResolver,
        IReadOnlySet<string>? scopeDocuments,
        Dictionary<SyntaxTree, SemanticModel> semanticModelCache,
        BindingIncompletenessCollector? incompleteness = null)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        LocationResolver = locationResolver;
        ScopeDocuments = scopeDocuments;
        _semanticModelCache = semanticModelCache;
        Incompleteness = incompleteness;
    }

    public Compilation Compilation { get; }
    public string SnapshotId { get; }
    public EdgeLocationResolver LocationResolver { get; }

    /// <summary>
    /// Collector for unobservable-binding records, shared with the workspace
    /// extractors so adapter-detected incompleteness lands in the same persisted
    /// vocabulary. Null in unit-test contexts that bypass
    /// <see cref="Lurp.Workspace.CompilationFactExtractor.ExtractAll"/>.
    /// </summary>
    internal BindingIncompletenessCollector? Incompleteness { get; }

    /// <summary>
    /// Absolute, forward-slash-normalized document paths; null means the whole compilation.
    /// Honored by every adapter, including <c>EfCoreAdapter</c>, whose annotations carry
    /// the evidence document and are retired by
    /// <c>IIndexStore.DeleteAnnotationsByDocumentPaths</c> over the same scope.
    /// </summary>
    public IReadOnlySet<string>? ScopeDocuments { get; }

    public SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        if (!_semanticModelCache.TryGetValue(tree, out var model))
        {
            model = Compilation.GetSemanticModel(tree);
            _semanticModelCache[tree] = model;
        }
        return model;
    }

    public bool IsInScope(SyntaxTree? syntaxTree)
    {
        // Byte-identical semantics to SymbolExtractionContext.IsInScope:26-34 —
        // absolute path compare, path-less trees always in scope.
        if (ScopeDocuments == null || syntaxTree == null)
            return true;
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return true;
        return ScopeDocuments.Contains(filePath.Replace('\\', '/'));
    }

    /// <summary>
    /// True when any declaring part of <paramref name="symbol"/> is in scope. Used by
    /// the symbol-driven adapters, which have no syntax tree at the point they decide
    /// whether to walk a type.
    /// </summary>
    /// <remarks>
    /// Any-part rather than all-parts matches the type-level granularity of the
    /// Workspace guards, and costs nothing in practice: the incremental extraction
    /// scope is already widened to every document declaring a part of a touched type
    /// (<c>IncrementalIndexer.ExpandToDeclaringTypeParts</c>), so parts are in scope
    /// together or not at all. An implicitly-declared symbol has no document to scope
    /// by and stays in scope.
    /// </remarks>
    public bool IsSymbolInScope(ISymbol? symbol)
    {
        if (ScopeDocuments == null || symbol == null)
            return true;

        var references = symbol.DeclaringSyntaxReferences;
        if (references.Length == 0)
            return true;

        foreach (var reference in references)
        {
            if (IsInScope(reference.SyntaxTree))
                return true;
        }
        return false;
    }
}
