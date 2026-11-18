using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Drops the write-only columns (<c>documents.last_changed_snapshot_id</c>,
///     <c>symbols.fqn</c>, <c>symbols.metadata_json</c>) and the unused tables
///     (<c>graph_nodes</c>, <c>partial_declarations</c>) per D153.
/// </summary>
public sealed class Migration_039_DropWriteOnlyColumns : IMigration
{
    public int Version => 39;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX idx_symbols_fqn;
            DROP TABLE graph_nodes;
            DROP TABLE partial_declarations;
            ALTER TABLE documents DROP COLUMN last_changed_snapshot_id;
            ALTER TABLE symbols DROP COLUMN fqn;
            ALTER TABLE symbols DROP COLUMN metadata_json;
            """;
        command.ExecuteNonQuery();
    }
}
