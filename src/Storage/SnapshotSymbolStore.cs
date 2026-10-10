using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Lurp.Storage;

internal sealed class SnapshotSymbolStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal void SaveSnapshotSymbols(string snapshotId, IEnumerable<string> symbolIds)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            foreach (var symbolId in symbolIds)
            {
                command.CommandText = """
                    INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                    SELECT @snapshotId, @symbolId, fqn, metadata_json
                    FROM snapshot_symbols
                    WHERE symbol_id = @symbolId
                    LIMIT 1
                    ON CONFLICT(snapshot_id, symbol_id) DO UPDATE SET
                        fqn = excluded.fqn,
                        metadata_json = excluded.metadata_json;
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@symbolId", symbolId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal void CopySnapshotSymbols(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
            SELECT @toSnapshotId, symbol_id, fqn, metadata_json
            FROM snapshot_symbols
            WHERE snapshot_id = @fromSnapshotId
            ON CONFLICT(snapshot_id, symbol_id) DO UPDATE SET
                fqn = excluded.fqn,
                metadata_json = excluded.metadata_json;
            """;
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    internal void CopySymbolTargetFrameworks(string fromSnapshotId, string toSnapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO symbol_target_frameworks (snapshot_id, symbol_id, tfm)
            SELECT @toSnapshotId, symbol_id, tfm
            FROM symbol_target_frameworks
            WHERE snapshot_id = @fromSnapshotId;
            """;
        command.Parameters.AddWithValue("@fromSnapshotId", fromSnapshotId);
        command.Parameters.AddWithValue("@toSnapshotId", toSnapshotId);
        command.ExecuteNonQuery();
    }

    internal void DeleteSnapshotSymbolsBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
    {
        var idList = symbolIds as IReadOnlyCollection<string> ?? [.. symbolIds];
        if (idList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM snapshot_symbols
                WHERE snapshot_id = @snapshotId
                  AND symbol_id IN (SELECT value FROM json_each(@symbolIds));
                """;
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            SqlIdList.Bind(command, "@symbolIds", idList);
            command.ExecuteNonQuery();

            DeleteTargetFrameworkRows(command, snapshotId, idList);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal void DeleteSymbolTargetFrameworksBySymbolIds(string snapshotId, IEnumerable<string> symbolIds)
    {
        var idList = symbolIds as IReadOnlyCollection<string> ?? [.. symbolIds];
        if (idList.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;

            DeleteTargetFrameworkRows(command, snapshotId, idList);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    // The one symbol_target_frameworks delete text. It binds its own parameters
    // (@snapshotId and one JSON array of symbol ids), so both callers run it as-is.
    private static void DeleteTargetFrameworkRows(SqliteCommand command, string snapshotId, IReadOnlyCollection<string> idList)
    {
        command.Parameters.Clear();
        command.CommandText = """
            DELETE FROM symbol_target_frameworks
            WHERE snapshot_id = @snapshotId
              AND symbol_id IN (SELECT value FROM json_each(@symbolIds));
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@symbolIds", idList);
        command.ExecuteNonQuery();
    }

    internal List<string> GetSymbolIdsInSnapshot(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id
            FROM snapshot_symbols
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    internal int CountSymbolsInSnapshot(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}