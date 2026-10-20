namespace Lurp.Tests;

/// <summary>
///     CLI crash-path contract (audit A1 step 5): an unexpected exception must
///     produce a one-line diagnosis on stderr and the documented exit code 1,
///     never the runtime's unhandled-exception dump. The stack trace is opt-in via
///     <c>LURP_DEBUG=1</c> (or <c>--verbose</c>).
/// </summary>
public sealed class CliCrashPathTests
{
    [Fact]
    public void CorruptIndex_ExitsOne_WithOneLineAndNoStackTrace()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"lurp-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "index.db"), "this is not a sqlite database");
        try
        {
            var (exitCode, stderr) = Run(
                ["--mode=context", "--symbol=T:Crash.Probe|probe", $"--output-dir={outputDir}"],
                debug: false);

            Assert.Equal(1, exitCode);
            Assert.Contains("ERROR:", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.DoesNotContain("   at ", stderr);
            Assert.DoesNotContain("SqliteException", stderr);
        }
        finally
        {
            TestDatabase.DeleteDirectory(outputDir);
        }
    }

    [Fact]
    public void CorruptIndex_WithLurpDebug_PrintsStackTrace()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"lurp-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "index.db"), "this is not a sqlite database");
        try
        {
            var (exitCode, stderr) = Run(
                ["--mode=context", "--symbol=T:Crash.Probe|probe", $"--output-dir={outputDir}"],
                debug: true);

            Assert.Equal(1, exitCode);
            Assert.Contains("   at ", stderr);
            Assert.Contains("SqliteException", stderr);
        }
        finally
        {
            TestDatabase.DeleteDirectory(outputDir);
        }
    }

    [Fact]
    public void UnreadableWorkspace_ExitsTwo_WithMessageAndNoStackTrace()
    {
        var (solutionDir, outputDir) = CreateUnreadableSolution();
        var solutionPath = Path.Combine(solutionDir, "Test.slnx");
        try
        {
            var (exitCode, stderr) = Run(
                ["--mode=index", $"--solution={solutionPath}", $"--output-dir={outputDir}"],
                debug: false);

            Assert.Equal(2, exitCode);
            Assert.Contains("ERROR:", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.DoesNotContain("WorkspaceUnreadableException", stderr);
            Assert.DoesNotContain("   at ", stderr);
        }
        finally
        {
            Cleanup(solutionDir, outputDir, solutionPath);
        }
    }

    [Fact]
    public void UnreadableWorkspace_WithLurpDebug_PrintsStackTrace()
    {
        var (solutionDir, outputDir) = CreateUnreadableSolution();
        var solutionPath = Path.Combine(solutionDir, "Test.slnx");
        try
        {
            var (exitCode, stderr) = Run(
                ["--mode=index", $"--solution={solutionPath}", $"--output-dir={outputDir}"],
                debug: true);

            Assert.Equal(2, exitCode);
            Assert.Contains("WorkspaceUnreadableException", stderr);
            Assert.Contains("   at ", stderr);
        }
        finally
        {
            Cleanup(solutionDir, outputDir, solutionPath);
        }
    }

    // A project with no framework references has no corlib, so IndexRunner's gate
    // rejects the whole solution with WorkspaceUnreadableException and nothing is marked
    // complete. Same fixture as the blind-project store test.
    private static (string SolutionDir, string OutputDir) CreateUnreadableSolution()
    {
        var solutionDir = Path.Combine(Path.GetTempPath(), $"lurp-unreadable-{Guid.NewGuid():N}");
        var outputDir = Path.Combine(Path.GetTempPath(), $"lurp-unreadable-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(solutionDir);
        Directory.CreateDirectory(outputDir);
        IntegrationTestBase.WriteSolution(
            solutionDir,
            "Blind",
            new Dictionary<string, string> { ["Marker.cs"] = "namespace Blind; public class Marker { }" },
            msbuildProperties: new Dictionary<string, string> { ["DisableImplicitFrameworkReferences"] = "true" });
        return (solutionDir, outputDir);
    }

    private static void Cleanup(string solutionDir, string outputDir, string solutionPath)
    {
        TestDatabase.DeleteSolutionCache(solutionPath);
        TestDatabase.DeleteDirectory(solutionDir);
        TestDatabase.DeleteDirectory(outputDir);
    }

    private static (int ExitCode, string Stderr) Run(string[] args, bool debug)
    {
        var argv = LurpTool.Argv(args);
        var environment = debug ? new Dictionary<string, string> { ["LURP_DEBUG"] = "1" } : null;
        var (exitCode, _, stderr) = TestProcess.Run(argv[0], argv[1..], TimeSpan.FromMinutes(2), environment);
        return (exitCode, stderr);
    }
}
