using System.Diagnostics;

namespace Lurp.PerfHarness;

internal sealed record RunOutcome(
    long WallMs,
    long PeakWorkingSetMb,
    int ExitCode,
    bool TimedOut,
    string Stdout,
    string Stderr);

internal static class ProcessRunner
{
    internal const int PollIntervalMs = 50;

    public static RunOutcome Run(IReadOnlyList<string> argv, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new HarnessError($"could not start '{argv[0]}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var peakBytes = SamplePeak(process);
        var timedOut = false;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!process.WaitForExit(PollIntervalMs))
        {
            peakBytes = Math.Max(peakBytes, SamplePeak(process));
            if (Environment.TickCount64 < deadline)
                continue;

            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            break;
        }

        process.WaitForExit();
        var exitCode = process.ExitCode;
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        stopwatch.Stop();
        return new RunOutcome(stopwatch.ElapsedMilliseconds, ToMegabytes(peakBytes), exitCode, timedOut, stdout, stderr);
    }

    internal static long SamplePeak(Process process)
    {
        try
        {
            process.Refresh();
            return process.PeakWorkingSet64;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        catch (PlatformNotSupportedException)
        {
            return 0;
        }
    }

    internal static long ToMegabytes(long bytes) => (bytes + (1024L * 1024L) - 1) / (1024L * 1024L);
}
