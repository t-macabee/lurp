using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
            if (File.Exists(release))
                return release;

            var debug = Path.Combine(projectRoot, "src", "bin", "Debug", "net10.0", "Lurp.dll");
            if (File.Exists(debug))
                return debug;

            return release;
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
        Assert.Contains("Roslyn-based code indexer", stdOut);
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
}
