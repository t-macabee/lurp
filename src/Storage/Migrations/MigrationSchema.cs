using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

internal static class MigrationSchema
{
    internal static HashSet<string> ColumnNames(SqliteCommand command, string tableName)
    {
        // Migration callers pass a fixed table name. PRAGMA table_info requires an identifier.
        command.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
