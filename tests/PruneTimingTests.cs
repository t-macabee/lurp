using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Index runs record the snapshot prune as a <c>prune_snapshots</c> timing row and, on the
///     incremental path, the workspace-info cost as a <c>workspace_info</c> row on the snapshot
///     the run wrote. A no-op incremental run writes no snapshot and therefore no timing row.
/// </summary>
public sealed class PruneTimingTests : IntegrationTestBase
{
    private const string ProjectName = "PruneTimingProbe";

    private const string ProbeSource = """
        namespace PruneTimingProbe;

        internal class Probe
        {
            internal void A() { }
        }
        """;

    private const string ProbeSourceWithSecondMethod = """
        namespace PruneTimingProbe;

        internal class Probe
        {
            internal void A() { }
            internal void B() { }
        }
        """;

    [SkippableFact]
    public async Task FullIndex_WritesOnePruneSnapshotsRow()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        Assert.Equal(1L, CountStepRows(snapshotId, SnapshotTimingSteps.PruneSnapshots));
    }

    [SkippableFact]
    public async Task IncrementalIndex_WritesOnePruneSnapshotsRowAndOneWorkspaceInfoRow()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        var fullSnapshotId = await RunFullIndexAsync(DbPath);

        WriteFile(ProjectName, "Probe.cs", ProbeSourceWithSecondMethod);
        Touch(Path.Combine(TestDir, "src", ProjectName, "Probe.cs"));
        var newSnapshotId = await RunIncrementalViaRunnerAsync();

        Assert.NotEqual(fullSnapshotId, newSnapshotId);
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.PruneSnapshots));
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.WorkspaceInfo));
    }

    [SkippableFact]
    public async Task NoOpIncremental_PrecheckSkip_WritesNoSnapshotAndNoNewPruneSnapshotsRow()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        await RunFullIndexAsync(DbPath);

        var snapshotsBefore = CountSnapshots();
        var pruneRowsBefore = CountStepRowsInDatabase(SnapshotTimingSteps.PruneSnapshots);

        var sink = new CapturingOutputSink();
        await RunIncrementalViaRunnerAsync(sink);
        var output = sink.Output.ToString();

        Assert.Equal(snapshotsBefore, CountSnapshots());
        Assert.Equal(pruneRowsBefore, CountStepRowsInDatabase(SnapshotTimingSteps.PruneSnapshots));
        Assert.Contains("No changes detected. Skipping incremental index.", output);
        Assert.DoesNotContain("Hashing documents and detecting changes", output);
    }

    [SkippableFact]
    public async Task IncrementalIndex_WithNoContentChange_AddsNoTimingRowToTheExistingSnapshot()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        var fullSnapshotId = await RunFullIndexAsync(DbPath);

        var snapshotsBefore = CountSnapshots();
        var pruneRowsBefore = CountStepRowsInDatabase(SnapshotTimingSteps.PruneSnapshots);
        var workspaceInfoRowsBefore = CountStepRowsInDatabase(SnapshotTimingSteps.WorkspaceInfo);

        // A build input (.csproj) newer than the snapshot defeats the precheck
        // (WorkspaceFreshness.HasNoNewSourcesOrBuildInputs), but the content hashes are
        // unchanged, so IncrementalIndexer returns at changedDocs.Count == 0.
        var sink = new CapturingOutputSink();
        Touch(Path.Combine(TestDir, "src", ProjectName, ProjectName + ".csproj"));
        var latestSnapshotId = await RunIncrementalViaRunnerAsync(sink);
        var output = sink.Output.ToString();

        Assert.Equal(fullSnapshotId, latestSnapshotId);
        Assert.Equal(snapshotsBefore, CountSnapshots());
        Assert.Equal(pruneRowsBefore, CountStepRowsInDatabase(SnapshotTimingSteps.PruneSnapshots));
        Assert.Equal(workspaceInfoRowsBefore, CountStepRowsInDatabase(SnapshotTimingSteps.WorkspaceInfo));
        Assert.Contains("Hashing documents and detecting changes", output);
        Assert.DoesNotContain("Workspace load skipped: stored snapshot is still current.", output);
    }

    /// <summary>
    ///     Runs the incremental path through <see cref="IndexRunner" />, which is where the
    ///     prune and the incremental <c>workspace_info</c> row are recorded.
    /// </summary>
    private async Task<string> RunIncrementalViaRunnerAsync(IOutputSink? output = null)
    {
        using var store = OpenStore(DbPath);

        await IndexRunner.RunAsync(
            store, SolutionPath,
            [], null, "incremental",
            false, output, false, false, CancellationToken.None);

        var snapshot = store.LoadLatestSnapshot()
                       ?? throw new InvalidOperationException($"No snapshot found in {DbPath} after incremental index.");
        return snapshot.SnapshotId;
    }

    private static void Touch(string fullPath)
    {
        File.SetLastWriteTimeUtc(fullPath, DateTime.UtcNow.AddSeconds(10));
    }

    private long CountStepRows(string snapshotId, string stepName)
    {
        using var connection = OpenDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM snapshot_timings WHERE snapshot_id = @snapshotId AND step_name = @stepName;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@stepName", stepName);
        return (long)command.ExecuteScalar()!;
    }

    private long CountStepRowsInDatabase(string stepName)
    {
        using var connection = OpenDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshot_timings WHERE step_name = @stepName;";
        command.Parameters.AddWithValue("@stepName", stepName);
        return (long)command.ExecuteScalar()!;
    }

    private long CountSnapshots()
    {
        using var connection = OpenDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshots;";
        return (long)command.ExecuteScalar()!;
    }
}
