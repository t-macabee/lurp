using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

public sealed class Migration_052_WeightedSymbolFts : IMigration
{
    public int Version => 52;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE symbol_fts;
            CREATE VIRTUAL TABLE symbol_fts USING fts5(
                simple_name, name_tokens, container_tokens, fqn, doc_comment_id, kind,
                kind_rank UNINDEXED, is_test UNINDEXED, symbol_id UNINDEXED, snapshot_id UNINDEXED,
                tokenize='unicode61'
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT DISTINCT snapshot_id FROM snapshot_symbols WHERE fqn IS NOT NULL;";
        var snapshotIds = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                snapshotIds.Add(reader.GetString(0));
        }

        foreach (var snapshotId in snapshotIds)
            SearchIndexMaintenance.InsertSymbolRows(connection, snapshotId);
    }
}
