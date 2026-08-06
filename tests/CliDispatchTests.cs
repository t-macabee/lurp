using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Covers audit finding #45: the CLI dispatch surface (<c>Program.Main</c> /
/// <c>HandlerBootstrap</c> argument parsing) had no direct tests. These run the
/// built <c>Lurp.dll</c> as a subprocess rather than calling <c>Program.Main</c>
/// in-process, because the error paths call <see cref="Environment.Exit(int)"/>
/// directly, which would kill the test host.
/// </summary>
public sealed class CliDispatchTests
{
    private static string LurpDllPath
    {
        get
        {
            var assemblyDir = Path.GetDirectoryName(typeof(CliDispatchTests).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot determine test assembly location.");
            var projectRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));

            var release = Path.Combine(projectRoot, "src", "bin", "Release", "net10.0", "Lurp.dll");
            var debug = Path.Combine(projectRoot, "src", "bin", "Debug", "net10.0", "Lurp.dll");

            // Newest build wins. Preferring Release unconditionally let a stale Release
            // build shadow a freshly rebuilt Debug binary, so the tests exercised old
            // code after a fix (dotnet test rebuilds Debug, not Release).
            var newest = new[] { release, debug }
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return newest ?? release;
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
    {
        var dllPath = LurpDllPath;
        Assert.True(File.Exists(dllPath), $"Lurp.dll not found at {dllPath}. Build src/Lurp.csproj first.");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(dllPath);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)!;
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stdErr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exited = process.WaitForExit(30_000);
        Assert.True(exited, "lurp process did not exit within 30s.");
        // WaitForExit(int) can return before the async OutputDataReceived/ErrorDataReceived
        // callbacks finish flushing; the parameterless overload blocks until they drain.
        process.WaitForExit();

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    [Fact]
    public void NoArgs_PrintsHelp_ExitsZero()
    {
        var (exitCode, stdOut, _) = Run();

        Assert.Equal(0, exitCode);
        Assert.Contains("MODES", stdOut);
        Assert.Contains("--mode=index", stdOut);
    }

    [Fact]
    public void HelpFlag_PrintsHelp_ExitsZero()
    {
        var (exitCode, stdOut, _) = Run("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Roslyn-native semantic context engine for C#", stdOut);
    }

    [Fact]
    public void ModeHelp_PrintsHelp_ExitsZero()
    {
        var (exitCode, stdOut, _) = Run("--mode=help");

        Assert.Equal(0, exitCode);
        Assert.Contains("MODES", stdOut);
    }

    [Fact]
    public void UnknownMode_PrintsError_ExitsOne()
    {
        var (exitCode, _, stdErr) = Run("--mode=bogus");

        Assert.Equal(1, exitCode);
        Assert.Contains("ERROR: Unknown mode", stdErr);
    }

    [Fact]
    public void MissingModeFlag_PrintsError_ExitsOne()
    {
        var (exitCode, _, stdErr) = Run("--query=foo");

        Assert.Equal(1, exitCode);
        Assert.Contains("ERROR: Unknown mode", stdErr);
    }

    [Fact]
    public void Status_MissingOutputDir_PrintsError_ExitsOne()
    {
        var (exitCode, _, stdErr) = Run("--mode=status");

        Assert.Equal(1, exitCode);
        Assert.Contains("--output-dir", stdErr);
    }

    [Fact]
    public void GetSource_MissingOutputDir_PrintsError_ExitsOne()
    {
        var (exitCode, _, stdErr) = Run("--mode=get-source", "--document=Foo.cs");

        Assert.Equal(1, exitCode);
        Assert.Contains("--output-dir", stdErr);
    }

    /// <summary>
    /// Regression for the index-mode strategy refusal: <c>IndexRunner.ResolveStrategy</c>
    /// used to call <see cref="Environment.Exit(int)"/> from the Workspace layer (and once
    /// the exit moved to a thrown <see cref="InvalidOperationException"/>, a malformed
    /// value would crash with a stack trace). The CLI must refuse <c>--strategy=</c>
    /// through the normal <c>Fail</c> path: clean stderr message, exit 1, no trace.
    /// </summary>
    [Fact]
    public void Index_InvalidStrategy_PrintsCleanError_ExitsOne_NoStackTrace()
    {
        var anyExistingFile = typeof(CliDispatchTests).Assembly.Location;
        var (exitCode, _, stdErr) = Run(
            "--mode=index",
            "--strategy=bogus",
            $"--solution={anyExistingFile}",
            $"--output-dir={Path.Combine(Path.GetTempPath(), "lurp-strategy-test")}");

        Assert.Equal(1, exitCode);
        Assert.Contains("--strategy must be 'incremental' or 'full'", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
        Assert.DoesNotContain("InvalidOperationException", stdErr);
    }

    /// <summary>
    /// Regression for the flag-inventory bug: <c>--quiet</c> is read through
    /// <see cref="Lurp.Handlers.HandlerBootstrap.PrintFreshnessLine"/> on every freshness
    /// mode, but the search/find-symbol/impact registry entries did not list it, so the
    /// validator rejected a flag the handler consumes. Passing validation is proven by
    /// reaching the handler's own <c>--output-dir</c> refusal instead of the
    /// <c>unknown flag</c> refusal.
    /// </summary>
    [Theory]
    [InlineData("--mode=search", "--query=x")]
    [InlineData("--mode=find-symbol", "--fqn=X")]
    [InlineData("--mode=impact", "--symbol=X")]
    public void QuietFlag_IsNotRejectedAsUnknown(params string[] baseArgs)
    {
        var args = baseArgs.Concat(new[] { "--quiet" }).ToArray();
        var (exitCode, _, stdErr) = Run(args);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("unknown flag '--quiet'", stdErr);
        Assert.Contains("--output-dir", stdErr);
    }

    /// <summary>
    /// Regression for the flag-inventory bug: <c>--annotation-kind=</c> and <c>--value=</c>
    /// are written by <c>--mode=annotate</c> only; <c>--mode=get-annotations</c> never reads
    /// them, but its registry entry still advertised them, so the validator accepted a flag
    /// the handler silently ignored.
    /// </summary>
    [Theory]
    [InlineData("--annotation-kind=reviewed")]
    [InlineData("--value=looks good")]
    public void GetAnnotations_AnnotateOnlyFlag_IsRejectedAsUnknown(string flag)
    {
        var (exitCode, _, stdErr) = Run("--mode=get-annotations", flag);

        Assert.Equal(1, exitCode);
        Assert.Contains("unknown flag", stdErr);
        Assert.Contains(flag.Split('=')[0] + "=", stdErr);
        Assert.DoesNotContain("LURP_OUTPUT_DIR is required", stdErr);
    }

    /// <summary>
    /// Regression for a real crash hit while testing against an external solution:
    /// <c>SymbolId.Parse</c> throws an unhandled <see cref="FormatException"/> (raw
    /// stack trace on stdout, no exit-code discipline) when <c>--symbol</c> is a bare
    /// FQN/doc-comment ID instead of the 'docCommentId|assemblyIdentity' value
    /// <c>--mode=find-symbol</c> returns. <c>--mode=get-source</c> needs no live
    /// solution/database, so this doesn't need an indexed fixture to reach the check.
    /// </summary>
    [Fact]
    public void Context_MalformedSymbolId_PrintsCleanError_ExitsOne_NoStackTrace()
    {
        var (exitCode, _, stdErr) = Run(
            "--mode=context",
            "--symbol=T:eNote.Application.Features.Rentals.InstrumentRentals.Services.RentalCommandService",
            "--output-dir=.");

        Assert.Equal(1, exitCode);
        Assert.Contains("ERROR:", stdErr);
        Assert.Contains("not a resolvable symbolId", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
        Assert.DoesNotContain("FormatException", stdErr);
    }

    /// <summary>
    /// Regression for the same defect as <see cref="Context_MalformedSymbolId_PrintsCleanError_ExitsOne_NoStackTrace"/>,
    /// but through the <c>--tier=</c> continuation path: <c>ContextHandler.RunTierContinuation</c>
    /// calls <c>SymbolId.Parse</c> directly on a user-supplied <c>--symbol</c> without going
    /// through <c>ContextAssembler.ResolveAndAssemble</c>'s guarded path, so it did not inherit
    /// the fix above. Unlike the plain-context case, reaching this call requires an open store
    /// with a resolvable snapshot, so this needs a real (minimal) indexed fixture.
    /// </summary>
    [Fact]
    public void ContextTierContinuation_MalformedSymbolId_PrintsCleanError_ExitsOne_NoStackTrace()
    {
        var outputDir = CreateMinimalIndexDb();

        var (exitCode, _, stdErr) = Run(
            "--mode=context",
            "--symbol=T:eNote.Application.Features.Rentals.InstrumentRentals.Services.RentalCommandService",
            "--tier=directCallers",
            $"--output-dir={outputDir}");

        Assert.Equal(1, exitCode);
        Assert.Contains("ERROR:", stdErr);
        Assert.Contains("not a resolvable symbolId", stdErr);
        Assert.DoesNotContain("Unhandled exception", stdErr);
        Assert.DoesNotContain("FormatException", stdErr);
    }

    /// <summary>
    /// The smallest fixture that gets a subprocess CLI call past store-open and snapshot
    /// resolution: one workspace and one snapshot row, no symbols or edges. Sufficient because
    /// the regression above needs to reach <c>RunTierContinuation</c>'s validation, not any
    /// symbol data.
    /// </summary>
    private static string CreateMinimalIndexDb()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lurp-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");

        var seed = new SqliteIndexStore(dbPath);
        seed.Open();
        seed.RunMigrations();
        seed.Close();
        seed.Dispose();
        SqliteConnection.ClearAllPools();

        using (var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO workspaces (workspace_id, git_root, solution_path)
                VALUES ('ws-cli-dispatch', '/fake/root', 'test.sln');
                INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, status)
                VALUES ('snap-cli-dispatch', 'ws-cli-dispatch', '2026-01-01T00:00:00Z', 'complete');
            ";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        return dir;
    }
}
