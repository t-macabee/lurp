namespace Lurp.Storage;

/// <summary>
///     The generated-document filter of source search and grep (D4, D50, D165.4).
///     Each query that excludes generated documents appends this text. Migration
///     044 covers it with <c>idx_document_versions_generated</c>.
/// </summary>
internal static class GeneratedDocumentFilter
{
    /// <summary>Returns the clause (with a leading space) that drops generated document versions.</summary>
    internal static string ExcludeGeneratedDocuments(string documentVersionColumn) =>
        $" AND NOT EXISTS (SELECT 1 FROM document_versions dv_gen WHERE dv_gen.document_version_id = {documentVersionColumn} AND dv_gen.is_generated = 1)";
}
