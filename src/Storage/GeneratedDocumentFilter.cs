namespace Lurp.Storage;

/// <summary>
///     The generated-document filter of source search and grep. Each query that excludes
///     generated documents appends this text. Migration 035 covers it with
///     idx_declarations_doc_version_generated.
/// </summary>
internal static class GeneratedDocumentFilter
{
    /// <summary>Returns the clause (with a leading space) that drops documents with a generated declaration.</summary>
    internal static string ExcludeGeneratedDocuments(string documentVersionColumn) =>
        $" AND NOT EXISTS (SELECT 1 FROM declarations dec WHERE dec.document_version_id = {documentVersionColumn} AND dec.is_generated = 1)";
}
