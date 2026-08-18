using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Adds a covering index for the document-filtered annotation read introduced by
/// Gap 2 (annotations by file). Today only the by-symbol and whole-snapshot
/// access patterns existed; the new filter is <c>WHERE snapshot_id = @s AND
/// document_path = @p</c> ordered by <c>annotation_id</c>.
/// Also see six-gaps-in-lurp.md Gap 2: "New migration for
/// idx_annotations_snapshot_document(snapshot_id, document_path)".
/// </summary>
public sealed class Migration_028_AnnotationDocumentIndex : IMigration
{
    public int Version => 28;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_annotations_snapshot_document ON annotations(snapshot_id, document_path);";
        command.ExecuteNonQuery();
    }
}
