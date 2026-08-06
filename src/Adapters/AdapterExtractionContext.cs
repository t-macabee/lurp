using Microsoft.CodeAnalysis;
using Lurp.Shared;

namespace Lurp.Adapters;

public sealed class AdapterExtractionContext
{
    private readonly Dictionary<SyntaxTree, SemanticModel> _semanticModelCache;

    internal AdapterExtractionContext(
        Compilation compilation,
        string snapshotId,
        EdgeLocationResolver locationResolver,
        IReadOnlySet<string>? scopeDocuments,
        Dictionary<SyntaxTree, SemanticModel> semanticModelCache)
    {
        Compilation = compilation;
        SnapshotId = snapshotId;
        LocationResolver = locationResolver;
        ScopeDocuments = scopeDocuments;
        _semanticModelCache = semanticModelCache;
    }

    public Compilation Compilation { get; }
    public string SnapshotId { get; }
    public EdgeLocationResolver LocationResolver { get; }

    /// <summary>
    /// Absolute, forward-slash-normalized document paths; null means the whole compilation.
    /// Plumbed for task B6; no adapter honors it yet.
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
}
