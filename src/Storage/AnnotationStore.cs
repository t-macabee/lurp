using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class AnnotationStore
{
    private readonly SqliteConnection _connection;

    public AnnotationStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public void SaveAnnotations(string snapshotId, IEnumerable<AnnotationRecord> annotations)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var ann in annotations)
            {
                command.CommandText = @"
                    INSERT INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
                    VALUES (@snapshotId, @symbolId, @kind, @value, @documentPath);
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@symbolId", ann.SymbolId);
                command.Parameters.AddWithValue("@kind", ann.Kind);
                command.Parameters.AddWithValue("@value", ann.Value);
                command.Parameters.AddWithValue("@documentPath", (object?)ann.DocumentPath ?? DBNull.Value);
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

    public List<AnnotationRecord> GetAnnotations(string snapshotId, string? symbolId = null)
    {
        using var command = _connection.CreateCommand();
        if (symbolId != null)
        {
            command.CommandText = @"
                SELECT symbol_id, kind, value, document_path
                FROM annotations
                WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId
                ORDER BY annotation_id;
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }
        else
        {
            command.CommandText = @"
                SELECT symbol_id, kind, value, document_path
                FROM annotations
                WHERE snapshot_id = @snapshotId
                ORDER BY annotation_id;
            ";
        }

        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<AnnotationRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new AnnotationRecord(reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return results;
    }

    public void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO annotations (snapshot_id, symbol_id, kind, value, document_path)
            SELECT @toSnapshotId, symbol_id, kind, value, document_path
            FROM annotations
            WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    ///     Deletes annotations produced by walks anchored to the given documents.
    ///     Rows with a NULL <c>document_path</c> (user-authored annotations) are
    ///     never matched and survive untouched.
    /// </summary>
    public void DeleteAnnotationsByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths)
    {
        var pathList = documentPaths as IReadOnlyCollection<string> ?? documentPaths.ToList();
        if (pathList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                DELETE FROM annotations
                WHERE snapshot_id = @snapshotId
                  AND document_path IN (" + string.Join(", ", pathList.Select((_, i) => $"@p{i}")) + @");
            ";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            var i = 0;
            foreach (var path in pathList)
                command.Parameters.AddWithValue($"@p{i++}", path);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}