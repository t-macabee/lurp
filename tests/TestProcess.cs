using System.Diagnostics;

namespace Lurp.Tests;

internal static class TestProcess
{
    internal static (int ExitCode, string Stdout, string Stderr) Run(string fileName, string[] args, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // A reused MSBuild worker node inherits the child's redirected pipe
        // handles and keeps them open after `dotnet restore` exits, which blocks
        // ReadToEnd below until the node idles out (~15 minutes). Disable node
        // reuse for every child process so the streams close with the process.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (environment != null)
        {
            foreach (var pair in environment)
                psi.Environment[pair.Key] = pair.Value;
        }
        // Not shell injection: ArgumentList passes each element straight to the OS
        // process API with UseShellExecute = false, so nothing here is parsed by a
        // shell; the arguments are test-generated paths and fixed flags, not input.
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            // Kill throws InvalidOperationException when the process exits after the timeout and before Kill.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{fileName} {string.Join(' ', args)} did not exit within {timeout}.");
        }

        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    internal static void RestoreSolution(string solutionPath)
    {
        var (exitCode, stdout, stderr) = Run("dotnet", ["restore", solutionPath, "--nologo"], TimeSpan.FromMinutes(10));
        if (exitCode != 0)
            throw new InvalidOperationException($"dotnet restore failed ({exitCode}):\nstdout:\n{stdout}\nstderr:\n{stderr}");
    }
}
