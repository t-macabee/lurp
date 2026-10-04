using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_020_EdgeTypeArguments : IMigration
    {
        public int Version => 20;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            var existingColumns = GetColumnNames(command, "edges");

            if (!existingColumns.Contains("type_arguments_json"))
            {
                command.CommandText = "ALTER TABLE edges ADD COLUMN type_arguments_json TEXT;";
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
