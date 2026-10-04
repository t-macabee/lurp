using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_009_PerSnapshotSymbolData : IMigration
    {
        public int Version => 9;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            var existingColumns = GetColumnNames(command, "snapshot_symbols");

            if (!existingColumns.Contains("fqn"))
            {
                command.CommandText = @"
                    ALTER TABLE snapshot_symbols ADD COLUMN fqn TEXT;
                ";
                command.ExecuteNonQuery();
            }

            if (!existingColumns.Contains("metadata_json"))
            {
                command.CommandText = @"
                    ALTER TABLE snapshot_symbols ADD COLUMN metadata_json TEXT;
                ";
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
}
