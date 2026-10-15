using System.Text.Json.Nodes;

namespace Lurp.Tests;

/// <summary>
///     Helpers shared by the perf harness evaluator tests (GateEvaluatorTests and
///     BaselineEvaluatorTests): run the real harness as a process, quote a command-line
///     argument, write a result JSON file, and build an operation entry.
/// </summary>
internal static class PerfHarnessResults
{
    internal static (int Code, string Stdout, string Stderr) RunHarness(string[] args)
    {
        var harnessDll = Path.Combine(AppContext.BaseDirectory, "harness.dll");
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        };
        return TestProcess.Run("dotnet", [harnessDll, .. args], TimeSpan.FromMinutes(5), environment);
    }

    internal static string WriteResult(string directory, string fileName, JsonObject result)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, result.ToJsonString());
        return path;
    }

    internal static JsonObject Operation(string name, double wallMs, double peakMb, bool failed = false, bool skipped = false, string? skipReason = null)
        => new()
        {
            ["name"] = name,
            ["skipped"] = skipped,
            ["skip_reason"] = skipReason,
            ["failed"] = failed,
            ["median"] = new JsonObject
            {
                ["wall_ms"] = wallMs,
                ["peak_working_set_mb"] = peakMb
            }
        };
}
