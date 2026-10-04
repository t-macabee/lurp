using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_018_AddSkippedAdapters : IMigration
    {
        public int Version => 18;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            var existingColumns = GetColumnNames(command, "snapshots");

            if (!existingColumns.Contains("skipped_adapters"))
            {
                command.CommandText = @"
                    ALTER TABLE snapshots ADD COLUMN skipped_adapters TEXT NOT NULL DEFAULT '';
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
