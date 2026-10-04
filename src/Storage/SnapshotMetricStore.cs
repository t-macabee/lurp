using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotMetricStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal void SaveMetrics(string snapshotId, IReadOnlyDictionary<string, long> metrics)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR REPLACE INTO snapshot_metrics (snapshot_id, metric_name, value)
                VALUES (@snapshotId, @metricName, @value);
                """;

            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            var metricNameParam = command.CreateParameter();
            metricNameParam.ParameterName = "@metricName";
            command.Parameters.Add(metricNameParam);
            var valueParam = command.CreateParameter();
            valueParam.ParameterName = "@value";
            command.Parameters.Add(valueParam);

            foreach (var (metricName, value) in metrics)
            {
                metricNameParam.Value = metricName;
                valueParam.Value = value;
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

    internal Dictionary<string, long> GetMetrics(string snapshotId)
    {
        var results = new Dictionary<string, long>(StringComparer.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT metric_name, value
            FROM snapshot_metrics
            WHERE snapshot_id = @snapshotId
            ORDER BY metric_name;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            results[reader.GetString(0)] = reader.GetInt64(1);
        return results;
    }
}
