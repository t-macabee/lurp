using System.Diagnostics;
using System.Text;

namespace Lurp.Storage.Tests;

/// <summary>
/// Shared subprocess harness for CLI dispatch and flag-validation tests.
/// Runs the built <c>Lurp.dll</c> as a subprocess rather than calling
/// <c>Program.Main</c> in-process, because the error paths call
/// <see cref="Environment.Exit(int)"/> directly, which would kill the test host.
/// </summary>
internal static class LurpProcessHarness
{
    /// <summary>
    /// Finds the newest built <c>Lurp.dll</c> between Release and Debug.
    /// Newest build wins: <c>dotnet test</c> rebuilds Debug, not Release, so a
    /// stale Release binary must not shadow a freshly rebuilt Debug one.
    /// </summary>
    internal static string LurpDllPath
    {
        get
        {
            var assemblyDir = Path.GetDirectoryName(typeof(LurpProcessHarness).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot determine test assembly location.");
            var projectRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));

            var release = Path.Combine(projectRoot, "src", "bin", "Release", "net10.0", "Lurp.dll");
            var debug = Path.Combine(projectRoot, "src", "bin", "Debug", "net10.0", "Lurp.dll");

            var newest = new[] { release, debug }
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return newest ?? release;
        }
    }

    /// <summary>
    /// Spawns <c>Lurp.dll</c> as a subprocess and returns exit code, stdout,
    /// and stderr. Asserts the DLL exists and the process exits within 30s.
    /// </summary>
    internal static (int ExitCode, string StdOut, string StdErr) Run(params string[] args)
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
        process.WaitForExit();

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }
}
