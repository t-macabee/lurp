using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_011_SnapshotStatus : IMigration
    {
        public int Version => 11;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                ALTER TABLE snapshots ADD COLUMN status TEXT NOT NULL DEFAULT 'complete';
            ";
            command.ExecuteNonQuery();
        }
    }
}
