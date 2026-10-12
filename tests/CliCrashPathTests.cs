using System.Diagnostics;

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
    public async Task InvalidIntent_ExitsOne_WithOneLineAndNoStackTrace()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"lurp-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var (exitCode, stderr) = await RunAsync(
                ["--mode=context", "--intent=banana", "--symbol=T:Crash.Probe|probe", $"--output-dir={outputDir}"],
                debug: false);

            Assert.Equal(1, exitCode);
            Assert.Contains("ERROR:", stderr);
            Assert.DoesNotContain("Unhandled exception", stderr);
            Assert.DoesNotContain("   at ", stderr);
        }
        finally
        {
            try { Directory.Delete(outputDir, true); } catch { }
        }
    }

    [Fact]
    public async Task InvalidIntent_WithLurpDebug_PrintsStackTrace()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"lurp-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var (exitCode, stderr) = await RunAsync(
                ["--mode=context", "--intent=banana", "--symbol=T:Crash.Probe|probe", $"--output-dir={outputDir}"],
                debug: true);

            Assert.Equal(1, exitCode);
            Assert.Contains("   at ", stderr);
        }
        finally
        {
            try { Directory.Delete(outputDir, true); } catch { }
        }
    }

    private static async Task<(int ExitCode, string Stderr)> RunAsync(string[] args, bool debug)
    {
        var argv = LurpTool.Argv(args);
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        for (var i = 1; i < argv.Length; i++)
            psi.ArgumentList.Add(argv[i]);
        if (debug)
            psi.Environment["LURP_DEBUG"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Lurp process");
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stderrTask, stdoutTask);

        return (process.ExitCode, await stderrTask);
    }
}
