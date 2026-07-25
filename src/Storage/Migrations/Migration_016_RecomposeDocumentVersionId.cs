using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_016_RecomposeDocumentVersionId : IMigration
    {
        public int Version => 16;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA foreign_keys = OFF;";
            command.ExecuteNonQuery();

            command.CommandText = @"
                UPDATE declarations
                SET document_version_id = (
                    SELECT dv.document_id || ':' || dv.content_hash
                    FROM document_versions dv
                    WHERE dv.document_version_id = declarations.document_version_id
                )
                WHERE EXISTS (
                    SELECT 1 FROM document_versions dv
                    WHERE dv.document_version_id = declarations.document_version_id
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                UPDATE snapshot_documents
                SET document_version_id = (
                    SELECT dv.document_id || ':' || dv.content_hash
                    FROM document_versions dv
                    WHERE dv.document_version_id = snapshot_documents.document_version_id
                )
                WHERE EXISTS (
                    SELECT 1 FROM document_versions dv
                    WHERE dv.document_version_id = snapshot_documents.document_version_id
                );
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                UPDATE document_versions
                SET document_version_id = document_id || ':' || content_hash;
            ";
            command.ExecuteNonQuery();

            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
        }
    }
}
