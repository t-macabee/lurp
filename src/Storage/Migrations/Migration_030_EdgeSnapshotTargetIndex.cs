using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Adds a composite index for the incoming-edge lookup
/// <c>WHERE snapshot_id = @s AND target_symbol_id = @id</c> used by
/// <c>EdgeOperationsStore.GetIncomingEdges</c>. The planner previously fell back
/// to the single-column <c>idx_edges_target</c> because no leading
/// <c>snapshot_id</c> prefix existed on the target side. The outgoing lookup is
/// already covered by the <c>ux_edges_relation(snapshot_id,
/// source_symbol_id, ...)</c> prefix, so no source-side index is added.
/// </summary>
public sealed class Migration_030_EdgeSnapshotTargetIndex : IMigration
{
    public int Version => 30;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_edges_snapshot_target ON edges(snapshot_id, target_symbol_id);";
        command.ExecuteNonQuery();
    }
}
