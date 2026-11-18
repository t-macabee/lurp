using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>snapshots.selected_at_utc</c> (D45, D157.1) and backfills it from
///     <c>built_at_utc</c> so every recency query orders by the run that selected a snapshot.
/// </summary>
public sealed class Migration_041_SnapshotSelectedAt : IMigration
{
    public int Version => 41;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            ALTER TABLE snapshots ADD COLUMN selected_at_utc TEXT;
            UPDATE snapshots SET selected_at_utc = built_at_utc;
            """;
        command.ExecuteNonQuery();
    }
}
