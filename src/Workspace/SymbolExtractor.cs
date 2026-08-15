using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class SymbolExtractor
{
    private readonly SymbolExtractionContext _context;

    private readonly Action<string>? _logWarning;

    public SymbolExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId,
        Action<string>? logWarning = null,
        IReadOnlySet<string>? scopeDocuments = null)
        : this(compilation, documentContents, documentVersions, generatedDocuments, snapshotId, logWarning, scopeDocuments, null)
    {
    }

    internal SymbolExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId,
        Action<string>? logWarning,
        IReadOnlySet<string>? scopeDocuments,
        BindingIncompletenessCollector? incompleteness)
    {
        if (compilation == null) throw new ArgumentNullException(nameof(compilation));
        if (documentContents == null) throw new ArgumentNullException(nameof(documentContents));
        if (documentVersions == null) throw new ArgumentNullException(nameof(documentVersions));
        if (generatedDocuments == null) throw new ArgumentNullException(nameof(generatedDocuments));
        if (snapshotId == null) throw new ArgumentNullException(nameof(snapshotId));

        _context = new SymbolExtractionContext(compilation, documentContents, documentVersions, generatedDocuments, snapshotId, scopeDocuments, incompleteness);
        _logWarning = logWarning;
    }

    public List<SymbolDeclaration> ExtractAll()
    {
        return new SymbolDeclarationExtractor(_context, _logWarning).ExtractAll();
    }

    public List<EdgeRecord> ExtractEdges()
    {
        return new SymbolStructuralEdgeExtractor(_context).ExtractEdges();
    }
}