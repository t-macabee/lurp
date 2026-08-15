using Lurp.Handlers;
using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Smoke tests for the <see cref="CliExitException" /> contract: every
///     <see cref="HandlerBootstrap.Fail" /> caller reports failure by throwing the
///     typed exception (with the exit code) instead of terminating the process, so
///     the paths are unit-testable and <c>Program.Main</c> owns
///     <see cref="Environment.Exit(int)" />.
/// </summary>
public sealed class CliExitSmokeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-exit-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SqliteIndexStore OpenEmptyStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace("w1", "gitroot", "solution.sln", DateTime.UtcNow);
        return store;
    }

    [Fact]
    public void Fail_ThrowsCliExitException_WithMessageAndDefaultCode()
    {
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.Fail("ERROR: test failure"));
        Assert.Equal(1, ex.ExitCode);
        Assert.Equal("ERROR: test failure", ex.Message);
    }

    [Fact]
    public void Fail_ThrowsCliExitException_WithExplicitCode()
    {
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.Fail("ERROR: not fresh", 2));
        Assert.Equal(2, ex.ExitCode);
    }

    [Fact]
    public void ParseOutputMode_InvalidValue_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.ParseOutputMode(["--output=banana"]));
        Assert.Contains("--output must be one of", ex.Message);
    }

    [Fact]
    public void ParseOutputMode_JsonlDisallowed_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.ParseOutputMode(["--output=jsonl"], false));
        Assert.Contains("single document", ex.Message);
    }

    [Fact]
    public void RequireArg_Missing_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() =>
            HandlerBootstrap.RequireArg([], "--symbol=", "ERROR: --symbol is required."));
        Assert.Equal("ERROR: --symbol is required.", ex.Message);
    }

    [Fact]
    public void ParsePositiveIntArg_Invalid_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() =>
            HandlerBootstrap.ParsePositiveIntArg(["--limit=zero"], "--limit=", 20));
        Assert.Contains("must be a positive integer", ex.Message);
    }

    [Fact]
    public void ResolveOutputDir_NoSource_Throws()
    {
        Assert.Throws<CliExitException>(() => HandlerBootstrap.ResolveOutputDir([]));
    }

    [Fact]
    public void ResolveDbPath_MissingDatabase_Throws()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), $"lurp-nodb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.ResolveDbPath(emptyDir));
            Assert.Contains("Index database not found", ex.Message);
        }
        finally
        {
            Directory.Delete(emptyDir, true);
        }
    }

    [Fact]
    public void ResolveSnapshotId_NoSnapshots_Throws()
    {
        using var store = OpenEmptyStore();
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.ResolveSnapshotId(store, null));
        Assert.Contains("No snapshots found", ex.Message);
    }

    [Fact]
    public void ResolveSnapshotId_Latest_ResolvesToSameIdAsOmittingSnapshot()
    {
        using var store = OpenEmptyStore();
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = "s1",
            WorkspaceId = "w1",
            GitRoot = "gitroot",
            SolutionPath = "solution.sln",
            CreatedAtUtc = DateTime.UtcNow
        });
        store.MarkSnapshotComplete("s1");

        var byLatest = HandlerBootstrap.ResolveSnapshotId(store, "latest");
        var byNull = HandlerBootstrap.ResolveSnapshotId(store, null);

        Assert.Equal(byNull, byLatest);
    }

    [Fact]
    public void ResolveSymbolArg_Unresolvable_Throws()
    {
        using var store = OpenEmptyStore();
        Assert.Throws<CliExitException>(() => HandlerBootstrap.ResolveSymbolArg(store, "Some.Type", "s1"));
    }

    [Fact]
    public void CliFlagValidation_UnknownFlag_Throws()
    {
        var entry = new Program.ModeRegistryEntry("search", "help", ["--query="], _ => Task.CompletedTask);
        var ex = Assert.Throws<CliExitException>(() =>
            CliFlagValidation.Validate(entry, ["--query=x", "--bogus-flag=1"]));
        Assert.Contains("unknown flag '--bogus-flag='", ex.Message);
    }
}