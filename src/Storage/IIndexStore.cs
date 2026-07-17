using System;
using System.Collections.Generic;

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

    public sealed class SymbolInfo
    {
        public SymbolId SymbolId { get; }
        public SymbolKind Kind { get; }
        public string? FullyQualifiedName { get; }
        public string? MetadataJson { get; }
        public int DeclarationCount { get; }
        public bool IsPartial { get; }

        public SymbolInfo(
            SymbolId symbolId,
            SymbolKind kind,
            string? fullyQualifiedName,
            string? metadataJson,
            int declarationCount,
            bool isPartial)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind;
            FullyQualifiedName = fullyQualifiedName;
            MetadataJson = metadataJson;
            DeclarationCount = declarationCount;
            IsPartial = isPartial;
        }
    }

    public interface IIndexStore : ISnapshotStore, IDeclarationStore, IEdgeStore, ISearchStore, ISemanticDiffStore
    {
    }
}

