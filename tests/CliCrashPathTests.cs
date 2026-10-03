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
    private static string FindLurpDll()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Lurp.dll");
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate Lurp.dll for crash-path test.");
    }

    [Fact]
    public async Task InvalidIntent_ExitsOne_WithOneLineAndNoStackTrace()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"lurp-crash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var (exitCode, stderr) = await RunAsync(FindLurpDll(),
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
            var (exitCode, stderr) = await RunAsync(FindLurpDll(),
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

    private static async Task<(int ExitCode, string Stderr)> RunAsync(string lurpDll, string[] args, bool debug)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(lurpDll);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
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
