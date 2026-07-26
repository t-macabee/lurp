using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_018_AddSkippedAdapters : IMigration
    {
        public int Version => 18;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                ALTER TABLE snapshots ADD COLUMN skipped_adapters TEXT NOT NULL DEFAULT '';
            ";
            command.ExecuteNonQuery();
        }
    }
}
