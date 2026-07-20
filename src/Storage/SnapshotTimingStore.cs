using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotTimingStore(string dbPath)
{
    private readonly string _dbPath = dbPath;

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    internal void SaveTimings(string snapshotId, IEnumerable<SnapshotTimingRow> timings)
    {
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                INSERT INTO snapshot_timings (snapshot_id, step_name, elapsed_ms, created_at_utc)
                VALUES (@snapshotId, @stepName, @elapsedMs, @createdAtUtc);
            ";

            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            var stepNameParam = command.CreateParameter();
            stepNameParam.ParameterName = "@stepName";
            command.Parameters.Add(stepNameParam);
            var elapsedMsParam = command.CreateParameter();
            elapsedMsParam.ParameterName = "@elapsedMs";
            command.Parameters.Add(elapsedMsParam);
            command.Parameters.AddWithValue("@createdAtUtc", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

            foreach (var timing in timings)
            {
                stepNameParam.Value = timing.StepName;
                elapsedMsParam.Value = timing.ElapsedMs;
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

    internal List<SnapshotTimingRow> GetTimings(string snapshotId)
    {
        var results = new List<SnapshotTimingRow>();
        using var connection = CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT step_name, elapsed_ms, created_at_utc
            FROM snapshot_timings
            WHERE snapshot_id = @snapshotId
            ORDER BY timing_id;
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SnapshotTimingRow(
                stepName: reader.GetString(0),
                elapsedMs: reader.GetInt64(1),
                createdAtUtc: DateTime.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return results;
    }

}
