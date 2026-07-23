using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class DeclarationMaintenanceStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal void DeleteDeclarationsByDocumentVersionIds(IEnumerable<string> documentVersionIds)
    {
        var idList = documentVersionIds.ToList();
        if (idList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                DELETE FROM declarations
                WHERE document_version_id IN (" + string.Join(", ", idList.Select((_, i) => $"@p{i}")) + @")
                  AND document_version_id NOT IN (
                      SELECT DISTINCT document_version_id FROM snapshot_documents
                  );
            ";
            int i = 0;
            foreach (var id in idList)
                command.Parameters.AddWithValue($"@p{i++}", id);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal List<string> GetSymbolIdsByDocumentVersionIds(string snapshotId, IEnumerable<string> documentVersionIds)
    {
        var idList = documentVersionIds as IReadOnlyCollection<string> ?? documentVersionIds.ToList();
        if (idList.Count == 0)
            return [];

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT DISTINCT ss.symbol_id
            FROM snapshot_symbols ss
            JOIN declarations d ON d.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId
              AND d.document_version_id IN (" + string.Join(", ", idList.Select((_, i) => $"@p{i}")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        int i = 0;
        foreach (var id in idList)
            command.Parameters.AddWithValue($"@p{i++}", id);
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    internal string? ResolveSymbolByLocation(string relativePath, int line, string snapshotId, bool includeGenerated = false)
    {
        var (docVersionId, lineStarts) = GetDocumentLineStarts(relativePath, snapshotId);
        if (docVersionId == null || lineStarts == null || lineStarts.Length == 0)
            return null;

        int lineIndex = Math.Max(0, line - 1);
        if (lineIndex >= lineStarts.Length)
            return null;

        int byteOffset = lineStarts[lineIndex];

        return FindSymbolAtOffset(docVersionId, byteOffset, includeGenerated);
    }

    private (string? DocVersionId, int[]? LineStarts) GetDocumentLineStarts(string relativePath, string snapshotId)
    {
        using var getDocCmd = _connection.CreateCommand();
        getDocCmd.CommandText = @"
            SELECT dv.document_version_id, dv.line_starts
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND doc.relative_path = @relativePath
            LIMIT 1;
        ";
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

        return (docVersionId, JsonSerializer.Deserialize<int[]>(lineStartsJson));
    }

    private string? FindSymbolAtOffset(string docVersionId, int byteOffset, bool includeGenerated)
    {
        using var findCmd = _connection.CreateCommand();
        findCmd.CommandText = @"
            SELECT d.symbol_id
            FROM declarations d
            WHERE d.document_version_id = @docVersionId
              AND d.full_start IS NOT NULL
              AND d.full_end IS NOT NULL
              AND d.full_start <= @byteOffset
              AND d.full_end > @byteOffset
        ";

        if (!includeGenerated)
        {
            findCmd.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";
        }

        findCmd.CommandText += " ORDER BY (d.full_end - d.full_start) ASC LIMIT 1;";

        findCmd.Parameters.AddWithValue("@docVersionId", docVersionId);
        findCmd.Parameters.AddWithValue("@byteOffset", byteOffset);

        return findCmd.ExecuteScalar() as string;
    }
}
