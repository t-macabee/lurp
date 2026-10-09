using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_022_BindingIncompleteness : IMigration
{
    public int Version => 22;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE binding_incompleteness (
                snapshot_id TEXT NOT NULL,
                project_name TEXT NOT NULL,
                document_path TEXT NOT NULL DEFAULT '',
                reason TEXT NOT NULL,
                occurrence_count INTEGER NOT NULL CHECK (occurrence_count > 0),
                extractor_version TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, project_name, document_path, reason)
            );
            CREATE INDEX idx_binding_incompleteness_snapshot_reason
                ON binding_incompleteness (snapshot_id, reason);
        ";
        command.ExecuteNonQuery();
    }
}
