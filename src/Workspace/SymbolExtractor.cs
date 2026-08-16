using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class SymbolExtractor
{
    private readonly SymbolExtractionContext _context;

    internal SymbolExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
        IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
        IReadOnlySet<DocumentId> generatedDocuments,
        string snapshotId,
        IReadOnlySet<string>? scopeDocuments,
        BindingIncompletenessCollector? incompleteness)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(documentContents);
        ArgumentNullException.ThrowIfNull(documentVersions);
        ArgumentNullException.ThrowIfNull(generatedDocuments);
        ArgumentNullException.ThrowIfNull(snapshotId);

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