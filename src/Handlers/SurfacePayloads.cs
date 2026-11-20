namespace Lurp.Handlers;

internal static class SurfacePayloads
{
    internal static OutlineDeclarationPayload OutlineDeclaration(DeclarationOutlineEntry entry) =>
        new(entry.SymbolId, entry.Kind, entry.FullyQualifiedName, entry.StartLine, entry.EndLine,
            entry.SignatureStartLine, entry.NameStartLine, entry.IsPartial, entry.IsGenerated);

    internal static AnnotationPayload AnnotationRow(AnnotationRecord annotation) =>
        new(annotation.AnnotationId, annotation.SymbolId, annotation.Kind, annotation.Value, annotation.DocumentPath);

    internal static DiagnosticPayload DiagnosticRow(DiagnosticEntry entry)
    {
        var record = entry.Record;
        return new DiagnosticPayload(record.ProjectName, entry.NormalizedDocumentPath, entry.InSnapshot,
            record.Severity, record.Id, record.Message, LineNumbers.ToOneBased(record.StartLine),
            record.StartColumn, LineNumbers.ToOneBased(record.EndLine), record.EndColumn);
    }

    internal sealed record OutlineDeclarationPayload(
        string symbol_id, string kind, string? fully_qualified_name, int start_line, int end_line,
        int? signature_start_line, int? name_start_line, bool is_partial, bool is_generated);

    internal sealed record AnnotationPayload(
        long annotation_id, string symbol_id, string kind, string value, string? document_path);

    internal sealed record DiagnosticPayload(
        string project_name, string? document_path, bool? in_snapshot, string severity, string id,
        string message, int? start_line, int? start_column, int? end_line, int? end_column);
}
