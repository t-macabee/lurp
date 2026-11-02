using Lurp.Storage;

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

    [Fact]
    public async Task FullIndex_WritesPlannerStatistics()
    {
        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        AssertPlannerStatisticsWritten(snapshotId);
    }

    [Fact]
    public async Task IncrementalIndex_WritesPlannerStatistics()
    {
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
        Assert.Equal(1L, CountStepRows(snapshotId, SnapshotTimingSteps.PlannerStatistics));
    }
}
