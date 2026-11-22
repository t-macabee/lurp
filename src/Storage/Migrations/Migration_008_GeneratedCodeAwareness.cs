using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_008_GeneratedCodeAwareness : IMigration
    {
        public int Version => 8;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            var existingColumns = MigrationSchema.ColumnNames(command, "declarations");

            if (!existingColumns.Contains("is_generated"))
            {
                command.CommandText = @"ALTER TABLE declarations ADD COLUMN is_generated INTEGER NOT NULL DEFAULT 0;";
                command.ExecuteNonQuery();
            }

            if (!existingColumns.Contains("generator_identity"))
            {
                command.CommandText = @"ALTER TABLE declarations ADD COLUMN generator_identity TEXT;";
                command.ExecuteNonQuery();
            }

            command.CommandText = @"CREATE INDEX IF NOT EXISTS idx_declarations_generated ON declarations(is_generated);";
            command.ExecuteNonQuery();
        }
    }
}