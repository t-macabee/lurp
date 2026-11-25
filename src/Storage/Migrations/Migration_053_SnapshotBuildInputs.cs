using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_053_SnapshotBuildInputs : IMigration
{
    public int Version => 53;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshot_build_inputs (
                snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id) ON DELETE CASCADE,
                normalized_path TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, normalized_path)
            ) WITHOUT ROWID;
            """;
        command.ExecuteNonQuery();
    }
}
