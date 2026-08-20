using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_029_AddSnapshotPins : IMigration
{
    public int Version => 29;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshot_pins (
                workspace_id TEXT PRIMARY KEY REFERENCES workspaces(workspace_id),
                pinned_snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                pinned_at_utc TEXT NOT NULL,
                previous_pinned_snapshot_id TEXT REFERENCES snapshots(snapshot_id)
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_snapshot_pins_pinned_id ON snapshot_pins(pinned_snapshot_id);";
        command.ExecuteNonQuery();
    }
}
