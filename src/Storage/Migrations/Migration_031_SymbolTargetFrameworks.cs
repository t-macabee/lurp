using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
/// Records the set of target frameworks a symbol is compiled for, per snapshot.
/// Multi-target projects are indexed as a union; this table keeps the per-symbol
/// attribution so a future filter can join on it. The primary key makes the
/// write a union, independent of TFM order.
/// </summary>
public sealed class Migration_031_SymbolTargetFrameworks : IMigration
{
    public int Version => 31;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS symbol_target_frameworks (
                snapshot_id TEXT NOT NULL REFERENCES snapshots(snapshot_id),
                symbol_id TEXT NOT NULL,
                tfm TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, symbol_id, tfm)
            );
            """;
        command.ExecuteNonQuery();

        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_symbol_tfm_snapshot_tfm ON symbol_target_frameworks(snapshot_id, tfm);";
        command.ExecuteNonQuery();
    }
}
