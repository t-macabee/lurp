using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class SymbolExtractor
{
    private readonly SymbolExtractionContext _context;

    public SymbolExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId,
        IReadOnlySet<string>? scopeDocuments = null)
        : this(compilation, documentContents, documentVersions, generatedDocuments, snapshotId, scopeDocuments, null)
    {
    }

    internal SymbolExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId,
        IReadOnlySet<string>? scopeDocuments,
        BindingIncompletenessCollector? incompleteness)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (documentContents == null) throw new ArgumentNullException(nameof(documentContents));
        if (documentVersions == null) throw new ArgumentNullException(nameof(documentVersions));
        if (generatedDocuments == null) throw new ArgumentNullException(nameof(generatedDocuments));
        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));

        _context = new SymbolExtractionContext(compilation, documentContents, documentVersions, generatedDocuments, snapshotId, scopeDocuments, incompleteness);
    }

    public List<SymbolDeclaration> ExtractAll()
    {
        return new SymbolDeclarationExtractor(_context).ExtractAll();
    }

    public List<EdgeRecord> ExtractEdges()
    {
        return new SymbolStructuralEdgeExtractor(_context).ExtractEdges();
    }
}