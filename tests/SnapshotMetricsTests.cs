using Lurp.Storage;
using Lurp.Storage.Migrations;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Phase 8 (item 16) snapshot metrics: migration 033 creates
///     <c>snapshot_metrics</c>, the store round-trips it, both index strategies
///     record <c>peak_working_set_mb</c> and a <c>solution_load</c> timing row,
///     and snapshot deletion removes the metrics.
/// </summary>
public sealed class SnapshotMetricsTests : IntegrationTestBase
{
    [Fact]
    public void Migration_033_CreatesSnapshotMetricsTable_AndIsIdempotent()
    {
        var dbPath = Path.Combine(TestDir, "migration.db");
        var runner = new MigrationRunner(dbPath);
        runner.RunMigrations();

        Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

        using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='snapshot_metrics';";
            Assert.Equal(1L, (long)command.ExecuteScalar()!);

            command.CommandText =
                "SELECT COUNT(*) FROM pragma_table_info('snapshot_metrics') WHERE name IN ('snapshot_id', 'metric_name', 'value');";
            Assert.Equal(3L, (long)command.ExecuteScalar()!);

            // The migration itself must tolerate a second run.
            var migration = new Migration_033_SnapshotMetrics();
            migration.Up(connection);
            migration.Up(connection);
        }

        runner.RunMigrations();
        Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void SaveMetrics_GetMetrics_RoundTrips()
    {
        var dbPath = Path.Combine(TestDir, "metrics-store.db");
        using var store = OpenStore(dbPath);
        SaveSnapshot(store, "snap-metrics");

        store.SaveMetrics("snap-metrics", new Dictionary<string, long>
        {
            [SnapshotMetricNames.PeakWorkingSetMb] = 123
        });

        var metrics = store.GetMetrics("snap-metrics");
        Assert.Single(metrics);
        Assert.Equal(123, metrics[SnapshotMetricNames.PeakWorkingSetMb]);
    }

    [Fact]
    public void DeleteSnapshotData_RemovesMetrics()
    {
        var dbPath = Path.Combine(TestDir, "metrics-delete.db");
        using var store = OpenStore(dbPath);
        SaveSnapshot(store, "snap-delete");

        store.SaveMetrics("snap-delete", new Dictionary<string, long>
        {
            [SnapshotMetricNames.PeakWorkingSetMb] = 7
        });
        Assert.Single(store.GetMetrics("snap-delete"));

        store.DeleteSnapshotData("snap-delete");

        Assert.Empty(store.GetMetrics("snap-delete"));
    }

    [SkippableFact]
    public async Task FullAndIncrementalIndex_RecordPeakWorkingSetAndSolutionLoad()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("MetricsProj", new Dictionary<string, string>
        {
            ["Models.cs"] = "namespace MetricsProj { public class Foo { public void Bar() {} } }"
        });

        var fullSnapshot = await RunFullIndexAsync(DbPath);

        WriteFile("MetricsProj", "Models.cs",
            "namespace MetricsProj { public class Foo { public void Bar() {} public void Baz() {} } }");

        using (var store = OpenStore(DbPath))
        {
            await IndexRunner.RunAsync(
                store, SolutionPath, [], null, "incremental",
                false, null, false, false, CancellationToken.None);
        }

        using var reopened = OpenStore(DbPath);
        var latest = reopened.LoadLatestSnapshot()
                     ?? throw new InvalidOperationException("No snapshot found after incremental index.");
        Assert.NotEqual(fullSnapshot, latest.SnapshotId);

        foreach (var snapshotId in new[] { fullSnapshot, latest.SnapshotId })
        {
            var metrics = reopened.GetMetrics(snapshotId);
            Assert.True(metrics.TryGetValue(SnapshotMetricNames.PeakWorkingSetMb, out var peakWorkingSetMb),
                $"Snapshot {snapshotId} has no {SnapshotMetricNames.PeakWorkingSetMb} metric.");
            Assert.True(peakWorkingSetMb > 0);

            Assert.Contains(reopened.GetTimings(snapshotId), t => t.StepName == "solution_load");
        }
    }

    private void SaveSnapshot(SqliteIndexStore store, string snapshotId)
    {
        store.SaveWorkspace("ws-metrics", TestDir, SolutionPath);
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = "ws-metrics",
            GitRoot = TestDir,
            SolutionPath = SolutionPath,
            CreatedAtUtc = DateTime.UtcNow
        });
        store.MarkSnapshotComplete(snapshotId);
    }
}
