using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_054_AnalyzersSkipped : IMigration
    {
        public int Version => 54;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE snapshots ADD COLUMN analyzers_skipped INTEGER NOT NULL DEFAULT 0;
                """;
            command.ExecuteNonQuery();
        }
    }
}
