using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_024_FailedSnapshotTombstone : IMigration
{
    public int Version => 24;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE snapshots ADD COLUMN payload_pruned INTEGER NOT NULL DEFAULT 0;";
        command.ExecuteNonQuery();
    }
}
