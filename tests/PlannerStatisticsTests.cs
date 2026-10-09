using Lurp.Storage;
using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     The product writes planner statistics (<c>ANALYZE</c>) after each successful index run,
///     on both the full and the incremental path, and records the cost as a
///     <c>planner_statistics</c> timing row. These tests check that the statistics and the timing
///     row exist after each run, including when an incremental run follows a database without
///     statistics.
/// </summary>
public sealed class PlannerStatisticsTests : IntegrationTestBase
{
    private const string ProjectName = "PlannerStatisticsProbe";

    private const string ProbeSource = """
        namespace PlannerStatisticsProbe;

        internal class Probe
        {
            internal void A() { }
        }
        """;

    private const string ProbeSourceWithSecondMethod = """
        namespace PlannerStatisticsProbe;

        internal class Probe
        {
            internal void A() { }
            internal void B() { }
        }
        """;

    [SkippableFact]
    public async Task FullIndex_WritesPlannerStatistics()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        AssertPlannerStatisticsWritten(snapshotId);
    }

    [SkippableFact]
    public async Task IncrementalIndex_WritesPlannerStatistics()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        await RunFullIndexAsync(DbPath);

        // Simulate a database indexed before the product wrote statistics.
        DropPlannerStatistics();

        WriteFile(ProjectName, "Probe.cs", ProbeSourceWithSecondMethod);
        var snapshotId = await RunIncrementalIndexAsync();

        AssertPlannerStatisticsWritten(snapshotId);
    }

    private void AssertPlannerStatisticsWritten(string snapshotId)
    {
        Assert.True(CountPlannerStatisticsRows() > 0);
        Assert.Equal(1L, CountRows(snapshotId, SnapshotTimingSteps.PlannerStatistics));
    }

    private long CountRows(string snapshotId, string stepName)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM snapshot_timings WHERE snapshot_id = @snapshotId AND step_name = @stepName;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@stepName", stepName);
        return (long)command.ExecuteScalar()!;
    }
}
