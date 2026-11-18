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

    internal string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        return NavigateToLocation(relativePath, line, snapshotId, includeGenerated)?.SymbolId;
    }

    internal NavigationTarget? NavigateToLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        var (docVersionId, lineStarts) = GetDocumentLineStarts(relativePath, snapshotId);
        if (docVersionId == null || lineStarts == null || lineStarts.Length == 0)
            return null;

        var lineIndex = Math.Max(0, line - 1);
        if (lineIndex >= lineStarts.Length)
            return null;

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.symbol_id, d.full_start, d.full_end, d.name_start, d.name_end,
                   doc.relative_path, d.document_version_id
            FROM declarations d
            {SnapshotDeclarationLink.Join("d", "sdecl")}
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
            WHERE sd.snapshot_id = @snapshotId
              AND d.document_version_id = @docVersionId
              AND d.full_start IS NOT NULL AND d.full_end IS NOT NULL
              AND d.full_start <= @byteOffset AND d.full_end > @byteOffset
            """;
        if (!includeGenerated)
            command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
        // A primary constructor and its type share a span in an older snapshot; the id
        // tie-break keeps the answer for one snapshot the same on every run.
        command.CommandText += " ORDER BY (d.full_end - d.full_start) ASC, d.symbol_id ASC LIMIT 1;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@docVersionId", docVersionId);
        command.Parameters.AddWithValue("@byteOffset", lineStarts[lineIndex]);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var startLineIndex = SourceLineMap.FindLineIndex(lineStarts, reader.GetInt32(1));
        var endLineIndex = SourceLineMap.FindLineIndex(lineStarts, reader.GetInt32(2));
        var startLine = LineNumbers.ToOneBased(startLineIndex);
        var endLine = LineNumbers.ToOneBased(endLineIndex);

        return new NavigationTarget(
            reader.GetString(0), reader.GetString(5), reader.GetString(6),
            reader.GetInt32(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            startLine,
            endLine);
    }

    private (string? DocVersionId, int[]? LineStarts) GetDocumentLineStarts(string relativePath, string snapshotId)
    {
        using var getDocCmd = _connection.CreateCommand();
        getDocCmd.CommandText = """
            SELECT dv.document_version_id, dv.line_starts
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
        using (var reader = getDocCmd.ExecuteReader())
        {
            if (!reader.Read())
                return (null, null);
            docVersionId = reader.GetString(0);
            lineStartsJson = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (lineStartsJson == null)
            return (docVersionId, null);

        return (docVersionId, SourceLineMap.ParseLineStarts(lineStartsJson, docVersionId));
    }
}