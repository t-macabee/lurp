using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
///     Index runs record one timing row per runner step on the snapshot the run wrote. Both
///     paths write <c>workspace_info</c>, <c>orphan_edge_cleanup</c> and <c>prune_snapshots</c>;
///     the incremental path also writes <c>incremental_precheck</c>, <c>configuration_check</c>
///     and <c>change_scope</c>. A no-op incremental run writes no snapshot and therefore no timing row.
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
    public async Task FullIndex_WritesOneRowPerRunnerStep()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject(ProjectName, new Dictionary<string, string>
        {
            ["Probe.cs"] = ProbeSource
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        Assert.Equal(1L, CountStepRows(snapshotId, SnapshotTimingSteps.PruneSnapshots));
        Assert.Equal(1L, CountStepRows(snapshotId, SnapshotTimingSteps.OrphanEdgeCleanup));
        Assert.Equal(0L, CountStepRows(snapshotId, SnapshotTimingSteps.IncrementalPrecheck));
        Assert.Equal(0L, CountStepRows(snapshotId, SnapshotTimingSteps.ConfigurationCheck));
        Assert.Equal(0L, CountStepRows(snapshotId, SnapshotTimingSteps.ChangeScope));
    }

    [SkippableFact]
    public async Task IncrementalIndex_WritesOneRowPerRunnerStep()
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
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.IncrementalPrecheck));
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.ConfigurationCheck));
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.ChangeScope));
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.OrphanEdgeCleanup));
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
        var precheckRowsBefore = CountStepRowsInDatabase(SnapshotTimingSteps.IncrementalPrecheck);
        var configCheckRowsBefore = CountStepRowsInDatabase(SnapshotTimingSteps.ConfigurationCheck);

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
        Assert.Equal(precheckRowsBefore, CountStepRowsInDatabase(SnapshotTimingSteps.IncrementalPrecheck));
        Assert.Equal(configCheckRowsBefore, CountStepRowsInDatabase(SnapshotTimingSteps.ConfigurationCheck));
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

    private long CountSnapshots()
    {
        using var connection = OpenDbConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshots;";
        return (long)command.ExecuteScalar()!;
    }
}
