using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_023_FailedSnapshotState : IMigration
{
    public int Version => 23;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        var existingColumns = GetColumnNames(command, "snapshots");

        if (!existingColumns.Contains("failure_reason_code"))
        {
            command.CommandText = "ALTER TABLE snapshots ADD COLUMN failure_reason_code TEXT;";
            command.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("failure_message"))
        {
            command.CommandText = "ALTER TABLE snapshots ADD COLUMN failure_message TEXT;";
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
