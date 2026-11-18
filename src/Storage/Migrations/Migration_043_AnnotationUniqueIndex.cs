using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Deduplicates existing extractor annotations and enforces set semantics
///     via a partial unique index on rows with a non-null <c>document_path</c>
///     (D162). User annotations have <c>document_path IS NULL</c> and remain
///     unconstrained.
/// </summary>
public sealed class Migration_043_AnnotationUniqueIndex : IMigration
{
    public int Version => 43;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM annotations
            WHERE document_path IS NOT NULL
              AND annotation_id NOT IN (
                  SELECT MIN(annotation_id)
                  FROM annotations
                  WHERE document_path IS NOT NULL
                  GROUP BY snapshot_id, symbol_id, kind, value, document_path
              );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_annotations_extractor_fact
            ON annotations(snapshot_id, symbol_id, kind, value, document_path)
            WHERE document_path IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }
}
