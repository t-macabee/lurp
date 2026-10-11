using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// <c>document_versions.document_id</c> is a foreign key to <c>documents</c>, and no index covered it.
/// Every <c>documents</c> row the prune deleted made SQLite scan all of <c>document_versions</c> (I2 P5),
/// and the prune's pointer repair looks rows up by this column too (B59).
/// </summary>
public sealed class Migration_037_DocumentVersionsDocumentIdIndex : IMigration
{
    public int Version => 37;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_document_versions_document_id ON document_versions(document_id);
            """;
        command.ExecuteNonQuery();
    }
}
