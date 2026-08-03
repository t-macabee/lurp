using System.Reflection;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class CancellationPropagationTests : IDisposable
{
    private string? _testDir;
    private string? _dbPath;

    public void Dispose()
    {
        if (_testDir != null && Directory.Exists(_testDir))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [SkippableFact]
    public async Task IndexRunner_RunAsync_PreCanceledToken_ThrowsBeforeSnapshotCreation()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        var (dbPath, solutionPath, outputDir) = SetupFixture();

        using var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        store.ValidateSchema(VersionConstants.DatabaseSchemaVersion);

        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // pre-canceled token

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                IndexRunner.RunAsync(
                    store,
                    solutionPath,
                    outputDir,
                    skipAdapters: [],
                    jsonExportPath: null,
                    strategyArg: "full",
                    cancellationToken: cts.Token));
        }
        finally
        {
        }

        // Verify no snapshot row was created.
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots;";
        var count = (long)cmd.ExecuteScalar()!;
        Assert.Equal(0L, count);
    }

    [SkippableFact]
    public async Task IncrementalIndexer_RunIncrementalAsync_CanceledBeforeFinalization_PreservesPreviousLatestSnapshot()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        var (dbPath, solutionPath, outputDir) = SetupFixture();

        // Step 1: Do a full index to establish a baseline snapshot.
        var firstSnapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, outputDir);

        // Step 2: Modify a source file so incremental has something to do.
        var libraryCs = Path.Combine(_testDir!, "Library", "Class1.cs");
        File.AppendAllText(libraryCs, "\n// incremental change for cancellation test\n");
        RunGitCommand(_testDir!, "add -A");
        RunGitCommand(_testDir!, "commit -m change");

        // Step 3: Set up the real store and a proxy that cancels the token
        // when staging (CopyEdgesToSnapshot) is called : this ensures the
        // new snapshot is created before cancellation fires.
        using var innerStore = new SqliteIndexStore(dbPath);
        innerStore.Open();
        innerStore.RunMigrations();

        using var cts = new CancellationTokenSource();
        var proxy = DispatchProxy.Create<IIndexStore, CancelAtStagingStore>();
        ((CancelAtStagingStore)(object)proxy).SetInner(innerStore, cts);

        // Step 4: Run incremental through IndexRunner : cancellation fires
        // before FinalizeSnapshotAsync can mark the snapshot complete.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            IndexRunner.RunAsync(
                proxy,
                solutionPath,
                outputDir,
                skipAdapters: [],
                jsonExportPath: null,
                strategyArg: "incremental",
                cancellationToken: cts.Token));

        // Step 5: Verify the previous complete snapshot is still latest
        // and no new complete snapshot was created.
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id, status FROM snapshots WHERE status = 'complete' ORDER BY built_at_utc DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        var latestCompleteId = reader.GetString(0);
        Assert.Equal(firstSnapshotId, latestCompleteId);
    }

    private (string DbPath, string SolutionPath, string OutputDir) SetupFixture()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_cancel_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");

        var solutionPath = IntegrationHarness.CopyFixtureToTemp(_testDir);

        RunGitCommand(_testDir, "init");
        RunGitCommand(_testDir, "config user.email test@test.com");
        RunGitCommand(_testDir, "config user.name Test");
        RunGitCommand(_testDir, "add -A");
        RunGitCommand(_testDir, "commit -m init");

        RunDotNetBuild(solutionPath);

        return (_dbPath, solutionPath, _testDir);
    }

    private static void RunGitCommand(string workingDir, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(30_000);
    }

    private static void RunDotNetBuild(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"build \"{solutionPath}\" --no-restore -v q")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(120_000);
    }

    /// <summary>
    /// DispatchProxy that cancels a <see cref="CancellationTokenSource"/> when
    /// <see cref="IIndexStore.CopyEdgesToSnapshot"/> is called, simulating
    /// cancellation that arrives after snapshot staging but before finalization.
    /// </summary>
    private class CancelAtStagingStore : DispatchProxy
    {
        private object? _inner;
        private CancellationTokenSource? _cts;

        public void SetInner(object inner, CancellationTokenSource cts)
        {
            _inner = inner;
            _cts = cts;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null
                && targetMethod.Name == nameof(IIndexStore.CopyEdgesToSnapshot)
                && _cts != null)
            {
                // Cancel the token during staging so finalization never completes.
                // The incremental indexer's compensating catch will delete the
                // staged snapshot and rethrow the OperationCanceledException.
                _cts.Cancel();
            }

            return targetMethod?.Invoke(_inner, args);
        }
    }
}
