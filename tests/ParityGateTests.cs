using System.ComponentModel;

namespace Lurp.Tests;

[Trait("Category", "Slow")]
public sealed class ParityGateTests
{
    [Fact]
    public void Integrity_FailsWithADataErrorWhenACliModeFails()
    {
        var result = RunGate("integrity", ["-FailMode", "grep"]);
        AssertExit(2, result);
        Assert.Contains("FAILED grep", result.Stderr);
    }

    [Fact]
    public void Integrity_PassesWhenEveryModeSucceeds() => AssertExit(0, RunGate("integrity", []));

    [Fact]
    public void Recall_ReportsAMissingDeclaration()
    {
        var replacement = Path.Combine(ParityTestPaths.RepositoryRoot(), "tests", "Fixtures", "ParityGate", "Lib.NoGreet.cs");
        var result = RunGate("recall", ["-ReplaceFile", "App/Lib.cs=" + replacement], "--seed=1", "--sample=50");
        AssertExit(1, result);
        Assert.Contains("MISSING DECLARATION M:Smoke.App.Lib.Greet(System.String)", result.Stdout);
    }

    [Fact]
    public void Recall_PassesOnTheSmokeFixture()
    {
        var result = RunGate("recall", [], "--seed=1", "--sample=50");
        AssertExit(0, result);
        Assert.Contains("missing_declarations=0", result.Stdout);
    }

    private static void AssertExit(int expected, (int ExitCode, string Stdout, string Stderr) result) =>
        Assert.True(result.ExitCode == expected, $"Expected exit {expected}, got {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");

    private static (int ExitCode, string Stdout, string Stderr) RunGate(string command, string[] wrapperArgs, params string[] options)
    {
        try
        {
            TestProcess.Run("pwsh", ["-NoProfile", "-Version"], TimeSpan.FromSeconds(30));
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("The parity gate tests require pwsh on PATH.", ex);
        }

        var fixtures = Path.Combine(ParityTestPaths.RepositoryRoot(), "tests", "Fixtures");
        var copy = Path.Combine(Path.GetTempPath(), $"lurp-parity-gate-{Guid.NewGuid():N}");
        var solution = Path.Combine(copy, "Smoke.slnx");
        var wrapper = Path.Combine(fixtures, "ParityGate", "lurp-wrapper.ps1");
        string[] toolCommand = ["pwsh", "-NoProfile", "-File", wrapper, .. wrapperArgs, "-Tool", .. LurpTool.Argv()];
        var lurpCommand = string.Join(' ', toolCommand.Select(arg => $"\"{arg}\""));
        try
        {
            TargetTreeIntegrityTests.CopyTree(Path.Combine(fixtures, "Smoke"), copy);
            var gate = ParityTestPaths.BuiltTool("scripts/parity/gate", "gate");
            return TestProcess.Run("dotnet",
                [gate, command, "--solution=" + solution, "--lurp-cmd=" + lurpCommand, .. options],
                TimeSpan.FromMinutes(15));
        }
        finally
        {
            TestDatabase.DeleteSolutionCache(solution);
            TestDatabase.DeleteDirectory(copy);
        }
    }
}
