namespace Lurp.Storage
{
    public sealed class SourceSearchResult
    {
        public string DocumentPath { get; }
        public string Snippet { get; }

        public SourceSearchResult(string documentPath, string snippet)
        {
            DocumentPath = documentPath ?? throw new ArgumentNullException(nameof(documentPath));
            Snippet = snippet ?? throw new ArgumentNullException(nameof(snippet));
        }
    }

    public sealed class SymbolSearchResult
    {
        public string SymbolId { get; }
        public string FullyQualifiedName { get; }
        public string Kind { get; }
        public string DocCommentId { get; }

        public SymbolSearchResult(string symbolId, string fullyQualifiedName, string kind, string docCommentId)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            DocCommentId = docCommentId ?? throw new ArgumentNullException(nameof(docCommentId));
        }
    }

    public enum ViewKind
    {
        Declaration,
        Signature,
        Body,
        Name,
        ContainingType,
        Surrounding,
    }

    public sealed class IndexedSymbolInfo
    {
        public SymbolId SymbolId { get; }
        public IndexedSymbolKind Kind { get; }
        public string? FullyQualifiedName { get; }
        public string? MetadataJson { get; }
        public int DeclarationCount { get; }
        public bool IsPartial { get; }

        public IndexedSymbolInfo(SymbolId symbolId, IndexedSymbolKind kind, string? fullyQualifiedName, string? metadataJson, int declarationCount, bool isPartial)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind;
            FullyQualifiedName = fullyQualifiedName;
            MetadataJson = metadataJson;
            DeclarationCount = declarationCount;
            IsPartial = isPartial;
        }
    }

    public interface IIndexStore : ISnapshotStore, IDeclarationStore, IEdgeStore, ISearchStore, ISemanticDiffStore, ISemanticDiffReadStore, IBindingIncompletenessStore
    {
        /// <summary>
        /// Deletes edges, binding-incompleteness rows, and annotations for the
        /// given document paths in one transaction. Populates a temp table with
        /// the path set once and joins all three deletes against it, instead of
        /// each of <see cref="IEdgeStore.DeleteEdgesByDocumentPaths"/>,
        /// <see cref="IBindingIncompletenessStore.DeleteBindingIncompletenessByDocumentPaths"/>,
        /// and <see cref="IEdgeStore.DeleteAnnotationsByDocumentPaths"/> rebuilding
        /// its own IN-list (or, for binding-incompleteness, issuing one DELETE per
        /// path) over the same set. Callers scoping a delete to exactly one of
        /// those tables should keep using the individual methods.
        /// </summary>
        void DeleteFactsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths);
    }
}

