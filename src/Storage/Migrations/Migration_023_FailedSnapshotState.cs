using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_023_FailedSnapshotState : IMigration
{
    public int Version => 23;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE snapshots ADD COLUMN failure_reason_code TEXT;";
        command.ExecuteNonQuery();
        command.CommandText = "ALTER TABLE snapshots ADD COLUMN failure_message TEXT;";
        command.ExecuteNonQuery();
    }
}
