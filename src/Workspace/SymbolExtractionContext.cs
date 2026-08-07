using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class SymbolExtractionContext(
    Compilation compilation,
    IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
    IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
    IReadOnlySet<DocumentId> generatedDocuments,
    string snapshotId,
    IReadOnlySet<string>? scopeDocuments = null,
    BindingIncompletenessCollector? incompleteness = null)
{
    internal Compilation Compilation { get; } = compilation;
    internal IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> DocumentContents { get; } = documentContents;
    internal IReadOnlyDictionary<DocumentId, DocumentVersionId> DocumentVersions { get; } = documentVersions;
    internal IReadOnlySet<DocumentId> GeneratedDocuments { get; } = generatedDocuments;
    internal string AssemblyIdentity { get; } = compilation.Assembly.Identity.GetDisplayName();
    internal string SnapshotId { get; } = snapshotId;
    internal IReadOnlySet<string>? ScopeDocuments { get; } = scopeDocuments;
    internal BindingIncompletenessCollector? Incompleteness { get; } = incompleteness;

    private readonly Dictionary<string, DocumentId> _docIdByPath =
        documentContents.Keys.ToDictionary(k => k.ToString().Replace('\\', '/'), k => k);

    internal void RecordFilteredExternal(ISymbol resolvedTarget, SyntaxNode? node)
        => Incompleteness?.RecordFilteredExternal(resolvedTarget, node, Compilation);

    internal bool IsInScope(SyntaxTree? syntaxTree)
    {
        if (ScopeDocuments == null || syntaxTree == null)
            return true;
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return true;
        return ScopeDocuments.Contains(filePath.Replace('\\', '/'));
    }

    internal DocumentId? ResolveDocumentId(SyntaxTree syntaxTree)
    {
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = filePath.Replace('\\', '/');

        if (_docIdByPath.TryGetValue(normalized, out var exactMatch))
            return exactMatch;

        foreach (var docId in DocumentContents.Keys)
        {
            var docPath = docId.ToString().Replace('\\', '/');
            if (docPath == normalized || docPath.EndsWith("/" + normalized) || normalized.EndsWith("/" + docPath))
                return docId;
        }

        return null;
    }
}
