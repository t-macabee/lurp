using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_007_SemanticChanges : IMigration
    {
        public int Version => 7;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS semantic_changes (change_id         TEXT  PRIMARY KEY,from_snapshot_id  TEXT  NOT NULL,to_snapshot_id    TEXT  NOT NULL,change_type       TEXT  NOT NULL,symbol_id         TEXT  NOT NULL,detail_json       TEXT,created_at_utc    TEXT  NOT NULL);
            ";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_semantic_changes_to_snapshot ON semantic_changes(to_snapshot_id);";
            command.ExecuteNonQuery();

            command.CommandText = "CREATE INDEX IF NOT EXISTS idx_semantic_changes_from_to ON semantic_changes(from_snapshot_id, to_snapshot_id);";
            command.ExecuteNonQuery();
        }
    }
}
