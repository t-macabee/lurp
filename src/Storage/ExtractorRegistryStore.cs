using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class ExtractorRegistryStore
{
    private readonly SqliteConnection _connection;

    public ExtractorRegistryStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    public void UpsertExtractors(IEnumerable<(string Name, string Version, string Description)> extractors)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            foreach (var (name, version, description) in extractors)
            {
                command.CommandText = @"
                    DELETE FROM extractors
                    WHERE name = @name AND version != @version;
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@version", version);
                command.ExecuteNonQuery();

                command.CommandText = @"
                    INSERT INTO extractors (name, version, description)
                    SELECT @name, @version, @description
                    WHERE NOT EXISTS (
                        SELECT 1 FROM extractors
                        WHERE name = @name AND version = @version
                    );
                ";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@version", version);
                command.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
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

    public bool HasStaleExtractorVersions(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) FROM edges
            WHERE snapshot_id = @sid
            AND extractor_version NOT IN (SELECT version FROM extractors);
        ";
        command.Parameters.AddWithValue("@sid", snapshotId);
        return (long)command.ExecuteScalar()! > 0;
    }
}
