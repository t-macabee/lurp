using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Migrations;

/// <summary>
///     Adds <c>snapshot_symbols.bare_fqn</c> (the FQN without balanced type-parameter lists),
///     creates lookup indexes on <c>(snapshot_id, bare_fqn)</c> and <c>(snapshot_id, fqn)</c>,
///     and backfills <c>bare_fqn</c> for existing rows (D117, D181.1, D181.2).
/// </summary>
public sealed class Migration_047_SnapshotSymbolBareFqn : IMigration
{
    public int Version => 47;

    public void Up(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var existingColumns = GetColumnNames(command, "snapshot_symbols");
        if (!existingColumns.Contains("bare_fqn"))
        {
            command.CommandText = "ALTER TABLE snapshot_symbols ADD COLUMN bare_fqn TEXT;";
            command.ExecuteNonQuery();
        }

        command.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_snapshot_symbols_bare_fqn
                ON snapshot_symbols(snapshot_id, bare_fqn);
            CREATE INDEX IF NOT EXISTS idx_snapshot_symbols_fqn
                ON snapshot_symbols(snapshot_id, fqn);
            """;
        command.ExecuteNonQuery();

        command.CommandText = """
            UPDATE snapshot_symbols SET bare_fqn = fqn
            WHERE fqn NOT LIKE '%<%' AND fqn NOT LIKE '%>%';
            """;
        command.ExecuteNonQuery();

        var bracketedFqns = new List<(string SnapshotId, string Fqn)>();
        command.CommandText = """
            SELECT DISTINCT snapshot_id, fqn FROM snapshot_symbols
            WHERE fqn LIKE '%<%' OR fqn LIKE '%>%';
            """;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                bracketedFqns.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (bracketedFqns.Count == 0)
            return;

        using var updateCommand = connection.CreateCommand();
        updateCommand.CommandText = """
            UPDATE snapshot_symbols SET bare_fqn = @bareFqn
            WHERE snapshot_id = @snapshotId AND fqn = @fqn;
            """;
        var bareParam = updateCommand.Parameters.Add("@bareFqn", SqliteType.Text);
        var snapshotParam = updateCommand.Parameters.Add("@snapshotId", SqliteType.Text);
        var fqnParam = updateCommand.Parameters.Add("@fqn", SqliteType.Text);

        foreach (var (snapshotId, fqn) in bracketedFqns)
        {
            bareParam.Value = FqnForms.RemoveTypeParameterLists(fqn);
            snapshotParam.Value = snapshotId;
            fqnParam.Value = fqn;
            updateCommand.ExecuteNonQuery();
        }
    }

    private static HashSet<string> GetColumnNames(SqliteCommand command, string tableName)
    {
        command.CommandText = $"PRAGMA table_info({tableName});";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }
}
