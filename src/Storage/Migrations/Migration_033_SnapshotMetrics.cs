using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Records per-snapshot index-run metrics as name/value pairs. Kept separate
/// from snapshot_timings: the timings table's rows are summed into total_ms
/// and percent.
/// </summary>
public sealed class Migration_033_SnapshotMetrics : IMigration
{
    public int Version => 33;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshot_metrics (
                snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                metric_name TEXT NOT NULL,
                value INTEGER NOT NULL,
                PRIMARY KEY (snapshot_id, metric_name)
            );
            """;
        command.ExecuteNonQuery();
    }
}
