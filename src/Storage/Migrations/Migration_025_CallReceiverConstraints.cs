using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_025_CallReceiverConstraints : IMigration
{
    public int Version => 25;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var existingColumns = GetColumnNames(command, "edges");

        if (!existingColumns.Contains("receiver_type_constraints_json"))
        {
            command.CommandText = "ALTER TABLE edges ADD COLUMN receiver_type_constraints_json TEXT;";
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
