using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotDocumentStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal string? GetSource(string relativePath, string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT dv.content
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE d.relative_path = @relativePath
              AND sd.snapshot_id = @snapshotId;
        ";
        command.Parameters.AddWithValue("@relativePath", relativePath);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = command.ExecuteScalar();
        if (result == null || result == DBNull.Value)
            return null;

        var bytes = (byte[])result;
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    internal void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            foreach (var (docId, versionId) in entries)
            {
                command.CommandText = @"
                    INSERT OR REPLACE INTO snapshot_documents (snapshot_id, document_version_id)
                    VALUES (@snapshotId, @documentVersionId);
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@documentVersionId", versionId);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT d.relative_path, dv.document_version_id
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var result = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    internal List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
    {
        var pathList = documentPaths as IReadOnlyCollection<string> ?? documentPaths.ToList();
        if (pathList.Count == 0)
            return [];

        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT dv.document_version_id
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId
              AND d.relative_path IN (" + string.Join(", ", pathList.Select((_, i) => $"@p{i}")) + @");
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        int i = 0;
        foreach (var path in pathList)
            command.Parameters.AddWithValue($"@p{i++}", path);
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }
}
