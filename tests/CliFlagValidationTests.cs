using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Covers <c>CliFlagValidation</c> directly. Like <see cref="CliDispatchTests"/>, the
/// rejection paths run the built <c>Lurp.dll</c> as a subprocess because they call
/// <see cref="Environment.Exit(int)"/>, which would kill the test host.
/// <para>
/// The positive sweep enumerates <c>Program.ModeRegistry</c> itself (via
/// <c>InternalsVisibleTo</c>), so a flag added to a registry entry is covered without
/// touching this file. Note what that proves: every <em>declared</em> flag passes
/// validation — the inventory is self-consistent with the validator. It cannot prove
/// the declarations match what a handler actually reads; that correspondence was
/// re-derived by tracing handler consumption and only a handler-level test can pin it.
/// </para>
/// </summary>
public sealed class CliFlagValidationTests
{
    private static string LurpDllPath
    {
        get
        {
            var assemblyDir = Path.GetDirectoryName(typeof(CliFlagValidationTests).Assembly.Location)
                ?? throw new InvalidOperationException("Cannot determine test assembly location.");
            var projectRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", ".."));

            var release = Path.Combine(projectRoot, "src", "bin", "Release", "net10.0", "Lurp.dll");
            var debug = Path.Combine(projectRoot, "src", "bin", "Debug", "net10.0", "Lurp.dll");

            // Newest build wins, same as CliDispatchTests: dotnet test rebuilds Debug,
            // and a stale Release binary must not shadow it.
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
        process.WaitForExit();

        return (process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    public static TheoryData<string, string[]> AllModesWithDeclaredFlags()
    {
        var data = new TheoryData<string, string[]>();
        foreach (var entry in Lurp.Program.ModeRegistry)
        {
            // A valued flag gets a dummy value; a bare flag is passed as-is. The dummy
            // value may be semantically invalid — that is fine, because a post-validation
            // refusal (bad --output value, missing database, unparsable --line) proves the
            // flag got PAST validation, which is all this sweep asserts.
            var flags = entry.Flags
                .Select(f => f.EndsWith('=') ? f + "x" : f)
                .ToArray();
            data.Add(entry.Name, flags);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllModesWithDeclaredFlags))]
    public void EveryDeclaredFlag_PassesValidation(string mode, string[] declaredFlags)
    {
        var args = new[] { $"--mode={mode}" }.Concat(declaredFlags).ToArray();

        var (_, _, stdErr) = Run(args);

        Assert.DoesNotContain("unknown flag", stdErr);
    }

    [Fact]
    public void UnknownFlag_ExitsNonZero_AndNamesTheMode()
    {
        var (exitCode, _, stdErr) = Run("--mode=diff", "--bogus-flag=1");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--bogus-flag='", stdErr);
        Assert.Contains("--mode=diff", stdErr);
    }

    /// <summary>
    /// Shape mismatch, valued form of a bare flag: <c>--quiet=5</c> must not match the
    /// declared bare <c>--quiet</c>. Before the validator, <c>IsQuiet</c>'s exact
    /// <c>args.Contains("--quiet")</c> silently treated it as not-quiet.
    /// </summary>
    [Fact]
    public void ValuedFormOfBareFlag_IsRejected()
    {
        var (exitCode, _, stdErr) = Run("--mode=search", "--query=x", "--quiet=5");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--quiet='", stdErr);
    }

    /// <summary>
    /// Shape mismatch, bare form of a valued flag: a bare <c>--budget</c> must not match
    /// the declared <c>--budget=</c>. Before the validator, <c>GetArgValue</c> silently
    /// fell through to the default budget.
    /// </summary>
    [Fact]
    public void BareFormOfValuedFlag_IsRejected()
    {
        var (exitCode, _, stdErr) = Run("--mode=context", "--symbol=x", "--budget");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--budget'", stdErr);
    }

    [Fact]
    public void EditDistance1Typo_ProducesDidYouMeanLine()
    {
        var (exitCode, _, stdErr) = Run("--mode=context", "--symbol=x", "--budgett=100");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--budgett='", stdErr);
        Assert.Contains("Did you mean '--budget='?", stdErr);
    }
}
