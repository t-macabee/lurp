using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations
{
    public class Migration_019_SchemaHardening : IMigration
    {
        public int Version => 19;

        public void Up(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();

            // D2: Enforce snapshot-document binding uniqueness.
            command.CommandText = @"
                CREATE UNIQUE INDEX IF NOT EXISTS ux_snapshot_documents
                ON snapshot_documents(snapshot_id, document_version_id);
            ";
            command.ExecuteNonQuery();

            // D3: Enforce document_versions immutability at the schema level.
            command.CommandText = @"
                CREATE TRIGGER IF NOT EXISTS trg_document_versions_immutable
                BEFORE UPDATE ON document_versions
                BEGIN
                    SELECT RAISE(ABORT, 'document_versions is immutable');
                END;
            ";
            command.ExecuteNonQuery();
        }
    }
}
