using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_011_SnapshotStatus : IMigration
    {
        public int Version => 11;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            var existingColumns = GetColumnNames(command, "snapshots");

            if (!existingColumns.Contains("status"))
            {
                command.CommandText = @"
                    ALTER TABLE snapshots ADD COLUMN status TEXT NOT NULL DEFAULT 'complete';
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
