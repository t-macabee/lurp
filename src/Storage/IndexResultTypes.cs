namespace Lurp.Storage;

public sealed class SourceSearchResult
{
    public SourceSearchResult(string documentPath, string snippet)
    {
        DocumentPath = documentPath ?? throw new ArgumentNullException(nameof(documentPath));
        Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
    }

    public string DocumentPath { get; }
    public string Snippet { get; }
}

public sealed class SymbolSearchResult
{
    public SymbolSearchResult(string symbolId, string fullyQualifiedName, string kind, string docCommentId)
    {
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        DocCommentId = docCommentId ?? throw new ArgumentNullException(nameof(docCommentId));
    }

    public string SymbolId { get; }
    public string FullyQualifiedName { get; }
    public string Kind { get; }
    public string DocCommentId { get; }
}

public enum ViewKind
{
    Declaration,
    Signature,
    Body
}

public sealed class IndexedSymbolInfo
{
    public IndexedSymbolInfo(SymbolId symbolId, IndexedSymbolKind kind, string? fullyQualifiedName, string? metadataJson, int declarationCount, bool isPartial)
    {
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        Kind = kind;
        FullyQualifiedName = fullyQualifiedName;
        MetadataJson = metadataJson;
        DeclarationCount = declarationCount;
        IsPartial = isPartial;
    }

    public SymbolId SymbolId { get; }
    public IndexedSymbolKind Kind { get; }
    public string? FullyQualifiedName { get; }
    public string? MetadataJson { get; }
    public int DeclarationCount { get; }
    public bool IsPartial { get; }
}

/// <summary>The per-snapshot symbol fields that the semantic differ compares.</summary>
public sealed record SymbolDiffInfo(string DocCommentId, string? FullyQualifiedName, string? MetadataJson);
