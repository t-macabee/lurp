using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// The new index covers the generated-document filter of source search and grep, so the
/// plan does not depend on planner statistics. The dropped indexes are leading prefixes
/// of a wider index, or are replaced by the new one, and serve no query.
/// </summary>
public sealed class Migration_035_DeclarationIndexes : IMigration
{
    public int Version => 35;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        // idx_edges_snapshot_id and idx_annotations_snapshot_id stay, because an index on
        // snapshot_id alone is ordered by rowid and serves the reads ordered by edge_id and
        // annotation_id.
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_declarations_doc_version_generated ON declarations(document_version_id, is_generated);
            DROP INDEX IF EXISTS idx_declarations_doc_version;
            DROP INDEX IF EXISTS idx_declarations_generated;
            DROP INDEX IF EXISTS idx_declarations_symbol_id;
            DROP INDEX IF EXISTS idx_snapshot_symbols_snapshot_id;
            DROP INDEX IF EXISTS idx_snapshot_documents_snapshot_id;
            """;
        command.ExecuteNonQuery();
    }
}
