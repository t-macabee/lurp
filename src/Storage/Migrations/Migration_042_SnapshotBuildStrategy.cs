using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>snapshots.build_strategy</c> (D42, D158.4) defaulting to
///     <c>incremental</c> so unknown history is never trusted as a full build.
/// </summary>
public sealed class Migration_042_SnapshotBuildStrategy : IMigration
{
    public int Version => 42;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE snapshots ADD COLUMN build_strategy TEXT NOT NULL DEFAULT 'incremental';";
        command.ExecuteNonQuery();
    }
}
