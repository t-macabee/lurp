using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_010_AddLastChangedSnapshotId : IMigration
    {
        public int Version => 10;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            var existingColumns = GetColumnNames(command, "documents");

            if (!existingColumns.Contains("last_changed_snapshot_id"))
            {
                command.CommandText = @"
                    ALTER TABLE documents ADD COLUMN last_changed_snapshot_id TEXT;
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
