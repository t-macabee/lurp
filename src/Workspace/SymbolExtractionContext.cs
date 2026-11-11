using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

internal sealed class SymbolExtractionContext(
    ProjectExtractionInputs inputs,
    IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> documentContents,
    IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions,
    IReadOnlySet<DocumentId> generatedDocuments,
    string targetFramework)
    : ExtractionContextBase(inputs)
{
    private readonly Dictionary<string, DocumentId> _docIdByPath =
        BuildDocIdByPath(documentContents.Keys);

    internal IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)> DocumentContents { get; } = documentContents;
    internal IReadOnlyDictionary<DocumentId, DocumentVersionId> DocumentVersions { get; } = documentVersions;
    internal IReadOnlySet<DocumentId> GeneratedDocuments { get; } = generatedDocuments;
    internal string TargetFramework { get; } = targetFramework;

    private static Dictionary<string, DocumentId> BuildDocIdByPath(IEnumerable<DocumentId> documentIds)
    {
        var lookup = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        var suffixClaims = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);

        foreach (var docId in documentIds)
        {
            var path = PathNormalizer.ToForwardSlash(docId.ToString());
            lookup[path] = docId;

            var span = path.AsSpan();
            for (var i = 0; i < span.Length; i++)
                if (span[i] == '/')
                {
                    var suffix = span[(i + 1)..].ToString();
                    if (lookup.ContainsKey(suffix))
                        continue;
                    if (ambiguous.Contains(suffix))
                        continue;
                    if (suffixClaims.TryGetValue(suffix, out var existing))
                    {
                        if (!existing.Equals(docId))
                        {
                            suffixClaims.Remove(suffix);
                            ambiguous.Add(suffix);
                        }
                    }
                    else
                    {
                        suffixClaims[suffix] = docId;
                    }
                }
        }

        foreach (var kv in suffixClaims)
            lookup[kv.Key] = kv.Value;

        return lookup;
    }

    internal DocumentId? ResolveDocumentId(SyntaxTree syntaxTree)
    {
        var filePath = syntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            return null;

        var normalized = PathNormalizer.ToForwardSlash(filePath);

        if (_docIdByPath.TryGetValue(normalized, out var match))
            return match;

        var span = normalized.AsSpan();
        for (var i = 0; i < span.Length; i++)
            if (span[i] == '/')
            {
                var suffix = span[(i + 1)..].ToString();
                if (_docIdByPath.TryGetValue(suffix, out match))
                    return match;
            }

        return null;
    }
}