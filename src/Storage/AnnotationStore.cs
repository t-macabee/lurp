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
                    INSERT INTO annotations (snapshot_id, symbol_id, kind, value)
                    VALUES (@snapshotId, @symbolId, @kind, @value);
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@symbolId", ann.SymbolId);
                command.Parameters.AddWithValue("@kind", ann.Kind);
                command.Parameters.AddWithValue("@value", ann.Value);
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
                SELECT symbol_id, kind, value
                FROM annotations
                WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId
                ORDER BY annotation_id;
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
        }
        else
        {
            command.CommandText = @"
                SELECT symbol_id, kind, value
                FROM annotations
                WHERE snapshot_id = @snapshotId
                ORDER BY annotation_id;
            ";
        }
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<AnnotationRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new AnnotationRecord(symbolId: reader.GetString(0),
                kind: reader.GetString(1),
                value: reader.GetString(2)));
        }
        return results;
    }

    public void CopyAnnotationsToSnapshot(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO annotations (snapshot_id, symbol_id, kind, value)
            SELECT @toSnapshotId, symbol_id, kind, value
            FROM annotations
            WHERE snapshot_id = @fromSnapshotId;
        ";
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }
}