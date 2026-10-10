using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Every query on <c>edges</c> filters <c>snapshot_id</c> and uses <c>ux_edges_relation</c> or
/// <c>idx_edges_snapshot_target</c>; the two single-column indexes serve no query (I1 inspection,
/// B55). Each costs about a fifth of every edge copy and of the prune.
/// </summary>
public sealed class Migration_036_DropUnusedEdgeIndexes : IMigration
{
    public int Version => 36;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX IF EXISTS idx_edges_source;
            DROP INDEX IF EXISTS idx_edges_target;
            """;
        command.ExecuteNonQuery();
    }
}
