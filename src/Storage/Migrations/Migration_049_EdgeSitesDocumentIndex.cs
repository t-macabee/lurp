using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>Indexes edge sites by document for incremental retirement (D205).</summary>
public sealed class Migration_049_EdgeSitesDocumentIndex : IMigration
{
    public int Version => 49;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX idx_edge_sites_document ON edge_sites(document_path);
            DROP INDEX idx_edges_document;
            """;
        command.ExecuteNonQuery();
    }
}
