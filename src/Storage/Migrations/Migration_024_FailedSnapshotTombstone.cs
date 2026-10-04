using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_024_FailedSnapshotTombstone : IMigration
{
    public int Version => 24;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var existingColumns = GetColumnNames(command, "snapshots");

        if (!existingColumns.Contains("payload_pruned"))
        {
            command.CommandText = "ALTER TABLE snapshots ADD COLUMN payload_pruned INTEGER NOT NULL DEFAULT 0;";
            command.ExecuteNonQuery();
        }
    }

    private static HashSet<string> GetColumnNames(SqliteCommand command, string tableName)
    {
        command.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
