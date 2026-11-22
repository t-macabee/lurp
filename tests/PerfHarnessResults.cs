using System.Text.Json.Nodes;

namespace Lurp.Tests;

/// <summary>
///     Helpers shared by the perf harness evaluator tests (GateEvaluatorTests and
///     BaselineEvaluatorTests): run the real harness as a process, quote a command-line
///     argument, write a result JSON file, and build an operation entry.
/// </summary>
internal static class PerfHarnessResults
{
    internal const long MiB = 1024 * 1024;

    internal static long PeakBytes(double peakMb) => (long)Math.Round(peakMb * MiB, MidpointRounding.AwayFromZero);

    internal static long CeilingMb(long peakBytes) => (peakBytes + MiB - 1) / MiB;

    internal static (int Code, string Stdout, string Stderr) RunHarness(string[] args, Dictionary<string, string>? environment = null)
    {
        var harnessDll = Path.Combine(AppContext.BaseDirectory, "harness.dll");
        environment ??= new Dictionary<string, string>
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
    {
        var peakBytes = PeakBytes(peakMb);
        return new JsonObject
        {
            ["name"] = name,
            ["skipped"] = skipped,
            ["skip_reason"] = skipReason,
            ["failed"] = failed,
            ["runs"] = new JsonArray(Enumerable.Range(0, 5).Select(_ => (JsonNode)Run(wallMs, peakMb)).ToArray()),
            ["median"] = new JsonObject
            {
                ["wall_ms"] = wallMs,
                ["peak_working_set_bytes"] = peakBytes,
                ["peak_working_set_mb"] = CeilingMb(peakBytes)
            }
        };
    }

    internal static JsonObject Run(double wallMs, double peakMb, int exitCode = 0)
    {
        var peakBytes = PeakBytes(peakMb);
        return new JsonObject
        {
            ["wall_ms"] = wallMs,
            ["peak_working_set_bytes"] = peakBytes,
            ["peak_working_set_mb"] = CeilingMb(peakBytes),
            ["exit_code"] = exitCode
        };
    }

    internal static JsonObject WithPeakBytes(JsonObject operation, long peakBytes)
    {
        foreach (var entry in operation["runs"]!.AsArray().OfType<JsonObject>().Prepend((JsonObject)operation["median"]!))
            entry["peak_working_set_bytes"] = peakBytes;

        ((JsonObject)operation["median"]!)["peak_working_set_mb"] = CeilingMb(peakBytes);
        foreach (var run in operation["runs"]!.AsArray().OfType<JsonObject>())
            run["peak_working_set_mb"] = CeilingMb(peakBytes);
        return operation;
    }

    internal static JsonObject ToLegacy(JsonObject result)
    {
        result["schema_version"] = 1;
        foreach (var operation in result["operations"]!.AsArray().OfType<JsonObject>())
        {
            foreach (var entry in operation["runs"]!.AsArray().OfType<JsonObject>().Prepend((JsonObject?)operation["median"]).OfType<JsonObject>())
                entry.Remove("peak_working_set_bytes");
        }

        return result;
    }

    internal static JsonObject FindOperation(JsonObject result, string name)
        => result["operations"]!.AsArray().OfType<JsonObject>().Single(op => (string?)op["name"] == name);

    internal static void InvalidateOperation(JsonObject operation, string change)
    {
        switch (change)
        {
            case "failed":
                operation["failed"] = true;
                break;
            case "null":
                operation["median"] = null;
                break;
            case "zero":
                operation["median"]!["wall_ms"] = 0;
                foreach (var run in operation["runs"]!.AsArray())
                    run!["wall_ms"] = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
    }
}
