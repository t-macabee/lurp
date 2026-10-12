using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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
        TestDatabase.Delete(_dbPath);
    }

    private SqliteIndexStore OpenEmptyStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace("w1", "gitroot", "solution.sln");
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
    public void ParseFreshnessMode_UnknownValue_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.ParseFreshnessMode(["--freshness=banana"]));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("--freshness must be one of: auto, hash, off", ex.Message);
    }

    [Fact]
    public void ParseFreshnessMode_KnownValuesAndMissing_Parse()
    {
        Assert.Equal(FreshnessMode.Auto, HandlerBootstrap.ParseFreshnessMode([]));
        Assert.Equal(FreshnessMode.Auto, HandlerBootstrap.ParseFreshnessMode(["--freshness=auto"]));
        Assert.Equal(FreshnessMode.Hash, HandlerBootstrap.ParseFreshnessMode(["--freshness=HASH"]));
        Assert.Equal(FreshnessMode.Off, HandlerBootstrap.ParseFreshnessMode(["--freshness=off"]));
    }

    [Fact]
    public void ParseFreshnessMode_AllowedValuesOfTheFlagRecord_Parse()
    {
        foreach (var value in FlagCatalog.Freshness.AllowedValues!)
            HandlerBootstrap.ParseFreshnessMode([$"--freshness={value}"]);
    }

    // A directory that does not exist, so it holds no index.db (the value checks run before any database access).
    private static string NoIndexOutputDir() => Path.Combine(Path.GetTempPath(), $"lurp-exit-{Guid.NewGuid():N}");

    [Fact]
    public void SearchHandler_UnknownType_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() => SearchHandler.Run(["--query=x", "--type=banana"]));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("--type must be one of: all, source, symbol", ex.Message);
    }

    [Fact]
    public void SearchHandler_UppercaseType_PassesTheTypeCheck()
    {
        var ex = Assert.Throws<CliExitException>(() => SearchHandler.Run(["--query=x", "--type=ALL", $"--output-dir={NoIndexOutputDir()}"]));
        Assert.Contains("Index database not found", ex.Message);
    }

    [Fact]
    public void PinSnapshot_UnknownOutput_Throws()
    {
        var ex = Assert.Throws<CliExitException>(() => PinSnapshotHandler.Run(["--output=banana", $"--output-dir={NoIndexOutputDir()}"]));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("--output must be one of: summary, json", ex.Message);

        var jsonl = Assert.Throws<CliExitException>(() => PinSnapshotHandler.Run(["--output=jsonl", $"--output-dir={NoIndexOutputDir()}"]));
        Assert.Contains("jsonl is not supported", jsonl.Message);
    }

    [Fact]
    public void ImpactHandler_BadDirection_ThrowsCliExit()
    {
        var ex = Assert.Throws<CliExitException>(() => ImpactHandler.Run(["--symbol=x", "--direction=x"]));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("Invalid direction 'x'. Use 'upstream' or 'downstream'.", ex.Message);
    }

    [Fact]
    public void ContextHandler_BadIntent_ThrowsCliExit()
    {
        var ex = Assert.Throws<CliExitException>(() => ContextHandler.Run(["--symbol=T:Crash.Probe|probe", "--intent=banana", $"--output-dir={NoIndexOutputDir()}"]));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("--intent must be one of: inspect, modify, diagnose.", ex.Message);
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
    public void ResolveOutputDir_SolutionOnly_UsesDefaultCache()
    {
        var solution = Path.Combine(Path.GetTempPath(), $"lurp-outdir-{Guid.NewGuid():N}", "Test.slnx");

        var resolved = HandlerBootstrap.ResolveOutputDir([$"--solution={solution}"]);

        Assert.Equal(LurpCache.ResolveSolutionCacheDir(solution), resolved);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(Path.Combine(localAppData, "lurp"), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveOutputDir_ExplicitInsideSolution_WarnsOnStderr()
    {
        var solutionDir = Path.Combine(Path.GetTempPath(), $"lurp-outdir-{Guid.NewGuid():N}");
        var solution = Path.Combine(solutionDir, "Test.slnx");

        string warning;
        string resolved;
        var writer = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(writer);
            resolved = HandlerBootstrap.ResolveOutputDir([$"--solution={solution}", $"--output-dir={solutionDir}"]);
        }
        finally
        {
            Console.SetError(original);
            warning = writer.ToString();
        }

        Assert.Equal(solutionDir, resolved);
        Assert.Contains("is inside the solution directory", warning);
    }

    [Fact]
    public void ResolveOutputDir_ExplicitOutsideSolution_DoesNotWarn()
    {
        var solutionDir = Path.Combine(Path.GetTempPath(), $"lurp-outdir-{Guid.NewGuid():N}");
        var solution = Path.Combine(solutionDir, "Test.slnx");
        var outputDir = solutionDir + "-out";

        string warning;
        var writer = new StringWriter();
        var original = Console.Error;
        try
        {
            Console.SetError(writer);
            var resolved = HandlerBootstrap.ResolveOutputDir([$"--solution={solution}", $"--output-dir={outputDir}"]);
            Assert.Equal(outputDir, resolved);
        }
        finally
        {
            Console.SetError(original);
            warning = writer.ToString();
        }

        Assert.DoesNotContain("is inside the solution directory", warning);
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
        var entry = new Program.ModeRegistryEntry("search", "help", [new FlagSpec("--query=", "x", "text")], _ => Task.CompletedTask);
        var ex = Assert.Throws<CliExitException>(() =>
            CliFlagValidation.Validate(entry, ["--query=x", "--bogus-flag=1"]));
        Assert.Contains("unknown flag '--bogus-flag='", ex.Message);
    }

    [Fact]
    public void CliFlagValidation_ValueFlagTwice_Throws()
    {
        var entry = new Program.ModeRegistryEntry("context", "help", [new FlagSpec("--symbol=", "x", "id")], _ => Task.CompletedTask);
        var ex = Assert.Throws<CliExitException>(() =>
            CliFlagValidation.Validate(entry, ["--symbol=a", "--symbol=b"]));
        Assert.Equal(1, ex.ExitCode);
        Assert.Contains("given more than once", ex.Message);
        Assert.Contains("--symbol=", ex.Message);
    }

    [Fact]
    public void CliFlagValidation_BooleanSwitchTwice_DoesNotThrow()
    {
        var entry = new Program.ModeRegistryEntry("status", "help", [new FlagSpec("--json", "x")], _ => Task.CompletedTask);
        CliFlagValidation.Validate(entry, ["--json", "--json"]);
    }

    [Fact]
    public void CliFlagValidation_RepeatableValueFlagTwice_DoesNotThrow()
    {
        var index = new Program.ModeRegistryEntry("index", "help", [new FlagSpec("--skip-adapter=", "x", "name", Repeatable: true)], _ => Task.CompletedTask);
        CliFlagValidation.Validate(index, ["--skip-adapter=A", "--skip-adapter=B"]);

        var context = new Program.ModeRegistryEntry("context", "help", [new FlagSpec("--affected-project=", "x", "name", Repeatable: true)], _ => Task.CompletedTask);
        CliFlagValidation.Validate(context, ["--affected-project=A", "--affected-project=B"]);
    }

    [Fact]
    public async Task StatusHandler_OutputMode_MatchesDocumentedVocabulary()
    {
        // The refactor routes status through HandlerBootstrap.ParseOutputMode; this pins the
        // documented contract: default summary text, --json alias for --output=json, and the
        // same jsonl/invalid rejections as every other single-document surface.
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var summaryDefault = await RunStatusCapturedAsync([$"--output-dir={dir}"]);
            Assert.Null(summaryDefault.Failure);
            Assert.Contains("Status: not indexed", summaryDefault.Stdout);

            var json = await RunStatusCapturedAsync([$"--output-dir={dir}", "--output=json"]);
            Assert.Null(json.Failure);
            using (var doc = JsonDocument.Parse(json.Stdout))
                Assert.True(doc.RootElement.TryGetProperty("indexed", out _));

            var jsonAlias = await RunStatusCapturedAsync([$"--output-dir={dir}", "--json"]);
            Assert.Null(jsonAlias.Failure);
            using (var docAlias = JsonDocument.Parse(jsonAlias.Stdout))
                Assert.True(docAlias.RootElement.TryGetProperty("indexed", out _));

            var summary = await RunStatusCapturedAsync([$"--output-dir={dir}", "--output=summary"]);
            Assert.Null(summary.Failure);
            Assert.Contains("Status: not indexed", summary.Stdout);

            var jsonl = await RunStatusCapturedAsync([$"--output-dir={dir}", "--output=jsonl"]);
            Assert.NotNull(jsonl.Failure);
            Assert.Contains("single document", jsonl.Failure!.Message);

            var invalid = await RunStatusCapturedAsync([$"--output-dir={dir}", "--output=banana"]);
            Assert.NotNull(invalid.Failure);
            Assert.Contains("--output must be one of: summary, json.", invalid.Failure.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_MissingFromSnapshot_Throws()
    {
        var dir = CreateIndexedDir(withSnapshot: false);
        try
        {
            var ex = Assert.Throws<CliExitException>(() =>
                DiffHandler.Run([$"--output-dir={dir}", "--from-snapshot=bogus", "--to-snapshot=also-bogus"]));
            Assert.Contains("snapshot 'bogus' not found", ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_MissingToSnapshot_Throws()
    {
        var dir = CreateIndexedDir(withSnapshot: true);
        try
        {
            var ex = Assert.Throws<CliExitException>(() =>
                DiffHandler.Run([$"--output-dir={dir}", "--from-snapshot=s1", "--to-snapshot=bogus"]));
            Assert.Contains("snapshot 'bogus' not found", ex.Message);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    // Regression: --snapshot=latest must resolve like every other read mode, not be
    // passed to GetTimings as a literal id.
    [Fact]
    public void Timings_SnapshotLatest_ResolvesToLatestSnapshot()
    {
        var dir = CreateIndexedDir(withSnapshot: true);
        try
        {
            using (var store = new SqliteIndexStore(Path.Combine(dir, "index.db")))
            {
                store.Open();
                store.SaveTimings("s1", [new SnapshotTimingRow("extraction_loop", 42)]);
            }

            var result = RunTimingsCaptured([$"--output-dir={dir}", "--snapshot=latest", "--json"]);

            Assert.Null(result.Failure);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal("s1", doc.RootElement.GetProperty("snapshot_id").GetString());
            Assert.Equal(42, doc.RootElement.GetProperty("steps")[0].GetProperty("elapsed_ms").GetInt64());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    ///     Creates a temp directory holding a migrated index.db (with the workspace
    ///     row DiffHandler's metadata join needs), optionally containing one complete
    ///     snapshot 's1', so <see cref="DiffHandler.Run" /> can resolve the database
    ///     through its own <c>--output-dir</c> path.
    /// </summary>
    private static string CreateIndexedDir(bool withSnapshot)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-diff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        using (var store = new SqliteIndexStore(dbPath))
        {
            store.Open();
            store.RunMigrations();
            store.SaveWorkspace("w1", "gitroot", "solution.sln");
            if (withSnapshot)
            {
                store.SaveSnapshot(new SnapshotRow
                {
                    SnapshotId = "s1",
                    WorkspaceId = "w1",
                    GitRoot = "gitroot",
                    SolutionPath = "solution.sln",
                    CreatedAtUtc = DateTime.UtcNow
                });
                store.MarkSnapshotComplete("s1");
            }
        }

        return dir;
    }

    private static async Task<(string Stdout, CliExitException? Failure)> RunStatusCapturedAsync(string[] args)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            await StatusHandler.Run(args);
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (stdout.ToString(), failure);
    }

    private static (string Stdout, CliExitException? Failure) RunTimingsCaptured(string[] args)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            TimingsHandler.Run(args);
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (stdout.ToString(), failure);
    }
}
