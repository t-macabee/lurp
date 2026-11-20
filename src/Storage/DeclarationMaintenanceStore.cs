using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeclarationMaintenanceStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal void CopySnapshotDeclarations(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO snapshot_declarations (snapshot_id, declaration_id)
            SELECT @toSnapshotId, sd_link.declaration_id
            FROM snapshot_declarations sd_link
            JOIN declarations d ON d.declaration_id = sd_link.declaration_id
            JOIN snapshot_documents sdoc
              ON sdoc.snapshot_id = @toSnapshotId
             AND sdoc.document_version_id = d.document_version_id
            WHERE sd_link.snapshot_id = @fromSnapshotId;
            """;
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    internal void DeleteSnapshotDeclarationsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
    {
        var idList = documentVersionIds as IReadOnlyCollection<string> ?? [.. documentVersionIds];
        if (idList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM snapshot_declarations
                WHERE snapshot_id = @snapshotId
                  AND declaration_id IN (
                      SELECT d.declaration_id
                      FROM declarations d
                      WHERE d.document_version_id IN (SELECT value FROM json_each(@documentVersionIds))
                  );
                """;
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            SqlIdList.Bind(command, "@documentVersionIds", idList);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // CROSS JOIN pins declarations as the outer table: the JSON list has no row estimate, so the
    // planner would otherwise scan the whole snapshot's snapshot_symbols (B16 U1b).
    internal static string SymbolIdsByDocumentVersionIdsSql() => $"""
        SELECT DISTINCT ss.symbol_id
        FROM declarations d
        CROSS {SnapshotDeclarationLink.Join("d", "sdecl")}
        CROSS JOIN snapshot_symbols ss
        WHERE ss.symbol_id = d.symbol_id
          AND ss.snapshot_id = @snapshotId
          AND d.document_version_id IN (SELECT value FROM json_each(@documentVersionIds));
        """;

    internal List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
    {
        var idList = documentVersionIds as IReadOnlyCollection<string> ?? [.. documentVersionIds];
        if (idList.Count == 0)
            return [];

        using var command = _connection.CreateCommand();
        command.CommandText = SymbolIdsByDocumentVersionIdsSql();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@documentVersionIds", idList);
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    internal NavigationTarget? NavigateToLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        var (docVersionId, lineStarts, byteCount) = GetDocumentLineStarts(relativePath, snapshotId);
        if (docVersionId == null || lineStarts == null || lineStarts.Length == 0)
            return null;

        var lineIndex = Math.Max(0, line - 1);
        if (lineIndex >= lineStarts.Length)
            return null;

        var lineStart = lineStarts[lineIndex];
        var lineEnd = lineIndex + 1 < lineStarts.Length
            ? lineStarts[lineIndex + 1]
            : byteCount ?? int.MaxValue;

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.symbol_id, d.full_start, d.full_end, d.name_start, d.name_end,
                   doc.relative_path, d.document_version_id, s.doc_comment_id, ss.metadata_json
            FROM declarations d
            {SnapshotDeclarationLink.Join("d", "sdecl")}
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
            JOIN symbols s ON s.symbol_id = d.symbol_id
            JOIN snapshot_symbols ss ON ss.symbol_id = d.symbol_id AND ss.snapshot_id = @snapshotId
            WHERE sd.snapshot_id = @snapshotId
              AND d.document_version_id = @docVersionId
              AND d.full_start IS NOT NULL AND d.full_end IS NOT NULL
              AND d.full_start < @lineEnd AND d.full_end > @lineStart
            """;
        if (!includeGenerated)
            command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
        command.CommandText += ";";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@docVersionId", docVersionId);
        command.Parameters.AddWithValue("@lineStart", lineStart);
        command.Parameters.AddWithValue("@lineEnd", lineEnd);

        var candidates = new List<NavigateCandidate>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var symbolId = reader.GetString(0);
                var fullStart = reader.GetInt32(1);
                var fullEnd = reader.GetInt32(2);
                var nameStart = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                var nameEnd = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                var docPath = reader.GetString(5);
                var declDocVersionId = reader.GetString(6);
                var docCommentId = reader.GetString(7);
                var metadataJson = reader.IsDBNull(8) ? null : reader.GetString(8);
                var metadata = SymbolMetadata.Parse(metadataJson, symbolId);

                candidates.Add(new NavigateCandidate(
                    symbolId,
                    docPath,
                    declDocVersionId,
                    fullStart,
                    fullEnd,
                    nameStart,
                    nameEnd,
                    SymbolMetadata.IsImplicitlyDeclared(metadata),
                    ClassifyKind(docCommentId, metadata)));
            }
        }

        if (candidates.Count == 0)
            return null;

        candidates.Sort(CompareCandidates);
        var best = candidates[0];

        var startLineIndex = SourceLineMap.FindLineIndex(lineStarts, best.FullStart);
        var endLineIndex = SourceLineMap.FindLineIndex(lineStarts, best.FullEnd);
        var startLine = LineNumbers.ToOneBased(startLineIndex);
        var endLine = LineNumbers.ToOneBased(endLineIndex);

        return new NavigationTarget(
            best.SymbolId, best.RelativePath, best.DocumentVersionId,
            best.FullStart, best.FullEnd,
            best.NameStart, best.NameEnd,
            startLine,
            endLine);
    }

    private static int CompareCandidates(NavigateCandidate a, NavigateCandidate b)
    {
        var cmp = a.SpanLength.CompareTo(b.SpanLength);
        if (cmp != 0) return cmp;
        cmp = a.IsImplicitlyDeclared.CompareTo(b.IsImplicitlyDeclared);
        if (cmp != 0) return cmp;
        cmp = a.KindClass.CompareTo(b.KindClass);
        if (cmp != 0) return cmp;
        return string.CompareOrdinal(a.SymbolId, b.SymbolId);
    }

    private static int ClassifyKind(string docCommentId, JsonElement? metadata)
    {
        if (docCommentId.StartsWith("T:", StringComparison.Ordinal))
            return 0;
        if (IsConstructorDocCommentId(docCommentId))
            return 1;
        if (SymbolMetadata.GetString(metadata, SymbolMetadataKeys.AccessorKind) is not null)
            return 3;
        return 2;
    }

    private static bool IsConstructorDocCommentId(string docCommentId)
    {
        if (!docCommentId.StartsWith("M:", StringComparison.Ordinal))
            return false;
        var paren = docCommentId.IndexOf('(', StringComparison.Ordinal);
        var withoutParams = paren >= 0 ? docCommentId.AsSpan(2, paren - 2) : docCommentId.AsSpan(2);
        var lastDot = withoutParams.LastIndexOf('.');
        var memberName = lastDot >= 0 ? withoutParams[(lastDot + 1)..] : withoutParams;
        return memberName.Equals("#ctor", StringComparison.Ordinal) || memberName.Equals("#cctor", StringComparison.Ordinal);
    }

    private (string? DocVersionId, int[]? LineStarts, int? ByteCount) GetDocumentLineStarts(string relativePath, string snapshotId)
    {
        using var getDocCmd = _connection.CreateCommand();
        getDocCmd.CommandText = """
            SELECT dv.document_version_id, dv.line_starts, COALESCE(dv.byte_count, LENGTH(dv.content))
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND doc.relative_path = @relativePath
            LIMIT 1;
            """;
        getDocCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        getDocCmd.Parameters.AddWithValue("@relativePath", relativePath);

        string? docVersionId;
        string? lineStartsJson;
        int? byteCount;
        using (var reader = getDocCmd.ExecuteReader())
        {
            if (!reader.Read())
                return (null, null, null);
            docVersionId = reader.GetString(0);
            lineStartsJson = reader.IsDBNull(1) ? null : reader.GetString(1);
            byteCount = reader.IsDBNull(2) ? null : reader.GetInt32(2);
        }

        if (lineStartsJson == null)
            return (docVersionId, null, byteCount);

        return (docVersionId, SourceLineMap.ParseLineStarts(lineStartsJson, docVersionId), byteCount);
    }

    private sealed record NavigateCandidate(
        string SymbolId,
        string RelativePath,
        string DocumentVersionId,
        int FullStart,
        int FullEnd,
        int NameStart,
        int NameEnd,
        bool IsImplicitlyDeclared,
        int KindClass)
    {
        public int SpanLength => FullEnd - FullStart;
    }
}