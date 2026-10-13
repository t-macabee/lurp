using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Lurp.Tests;

/// <summary>
///     Contract tests for the growth gate evaluator (scripts/perf/harness/GateEvaluator.cs).
///     Each test writes two small synthetic result files to a temp directory and runs
///     the real harness as a process, so the asserted exit codes are the ones the CLI
///     returns.
/// </summary>
public sealed class GateEvaluatorTests : IDisposable
{
    private static readonly string[] ComparedOperations =
    [
        "full_index", "incremental_1_file", "incremental_20_files", "diff", "dead_candidates",
        "search", "grep", "find_symbol", "mcp_status.default"
    ];

    private readonly string _tempDir;

    public GateEvaluatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lurp-gate-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AllRatiosThree_Passes()
    {
        var (code, stdout, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(0, code);
        Assert.Contains("gate verdict: PASS", stdout);
    }

    [Fact]
    public void TimeRatioSixOnFullIndex_Fails()
    {
        var ops16 = ComparedOperations.Select(name => name == "full_index" ? Op16(name, 600) : Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void TimeRatioSixOnSearch_PassesWithWarn()
    {
        var ops16 = ComparedOperations.Select(name => name == "search" ? Op16(name, 600) : Op16(name));
        var (code, stdout, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16));

        Assert.Equal(0, code);
        Assert.Contains("WARN", stdout);
    }

    [Fact]
    public void MemoryRatioSixOnSearch_Fails()
    {
        var ops16 = ComparedOperations.Select(name => name == "search" ? Op16(name, 300, 300) : Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void OperationMissingAt16x_Fails()
    {
        var ops16 = ComparedOperations.Where(name => name != "search").Select(name => Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void OperationMissingAt4x_Fails()
    {
        var ops4 = ComparedOperations.Where(name => name != "search").Select(name => Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void RequiredOperationMissingInBoth_Fails()
    {
        var ops4 = ComparedOperations.Where(name => name != "full_index").Select(name => Op4(name));
        var ops16 = ComparedOperations.Where(name => name != "full_index").Select(name => Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ops16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void FailedOperation_Fails()
    {
        var ops4 = ComparedOperations.Select(name => name == "full_index" ? Op4(name, failed: true) : Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void ProtocolNotGate_Fails()
    {
        var (code, _, _) = RunEvaluate(
            Result("custom", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void RunsDiffer_Fails()
    {
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 6, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void WarmupDiffers_Fails()
    {
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 2, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void UnreadableJson_Returns2()
    {
        var path4 = Path.Combine(_tempDir, "4x.json");
        File.WriteAllText(path4, "{not json");
        var path16 = PerfHarnessResults.WriteResult(_tempDir, "16x.json", Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        var (code, _, _) = PerfHarnessResults.RunHarness([$"--evaluate={path4},{path16}"]);

        Assert.Equal(2, code);
    }

    [Fact]
    public void MaxRatioThreeMakesRatioFourFail()
    {
        var ops16 = ComparedOperations.Select(name => name == "full_index" ? Op16(name, 400) : Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16),
            "--max-ratio=3");

        Assert.Equal(1, code);
    }

    [Fact]
    public void RatioExactlyAtLimit_Passes()
    {
        var ops16 = ComparedOperations.Select(name => Op16(name, 500, 250));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16));

        Assert.Equal(0, code);
    }

    [Fact]
    public void MedianZero_Fails()
    {
        var ops4 = ComparedOperations.Select(name => name == "full_index" ? Op4(name, 0) : Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void DocumentedSolutionOnlySkip_Passes()
    {
        var ops4 = ComparedOperations.Select(name => name == "search"
            ? Op4(name, skipped: true, skipReason: "no anchors available")
            : Op4(name));
        var ops16 = ComparedOperations.Select(name => name == "search"
            ? Op16(name, skipped: true, skipReason: "no anchors available")
            : Op16(name));
        var (code, stdout, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ops16));

        Assert.Equal(0, code);
        Assert.Contains("skipped as solution-only (not compared): search", stdout);
    }

    [Fact]
    public void UndocumentedSkip_Fails()
    {
        var ops4 = ComparedOperations.Select(name => name == "search"
            ? Op4(name, skipped: true, skipReason: "flaky")
            : Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));

        Assert.Equal(1, code);
    }

    [Fact]
    public void SummaryFile_MatchesStdout()
    {
        var path4 = PerfHarnessResults.WriteResult(_tempDir, "4x.json", Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))));
        var path16 = PerfHarnessResults.WriteResult(_tempDir, "16x.json", Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name))));
        var summaryPath = Path.Combine(_tempDir, "gate-summary.md");

        var (code, stdout, _) = PerfHarnessResults.RunHarness([$"--evaluate={path4},{path16}", $"--summary={summaryPath}"]);

        Assert.Equal(0, code);
        var summary = File.ReadAllText(summaryPath);
        Assert.True(stdout.StartsWith(summary, StringComparison.Ordinal),
            "stdout should start with the summary markdown");
    }

    [Fact]
    public void EvaluateOptionsWithoutEvaluate_Return2()
    {
        var (code1, _, _) = PerfHarnessResults.RunHarness(["--max-ratio=3"]);
        Assert.Equal(2, code1);

        var (code2, _, _) = PerfHarnessResults.RunHarness([$"--summary={Path.Combine(_tempDir, "summary.md")}"]);
        Assert.Equal(2, code2);
    }

    private (int Code, string Stdout, string Stderr) RunEvaluate(JsonObject result4, JsonObject result16, params string[] extraArgs)
    {
        var path4 = PerfHarnessResults.WriteResult(_tempDir, "4x.json", result4);
        var path16 = PerfHarnessResults.WriteResult(_tempDir, "16x.json", result16);
        return PerfHarnessResults.RunHarness([$"--evaluate={path4},{path16}", .. extraArgs]);
    }

    private static JsonObject Result(string protocol, int runs, int warmup, IEnumerable<JsonObject> operations)
    {
        var array = new JsonArray();
        foreach (var operation in operations)
            array.Add(operation);
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["protocol"] = protocol,
            ["runs"] = runs,
            ["warmup"] = warmup,
            ["operations"] = array
        };
    }

    // Base medians: 4x = 100 ms / 50 MB, 16x = 300 ms / 150 MB, so every ratio is 3.
    private static JsonObject Op4(string name, double wallMs = 100, double peakMb = 50, bool failed = false, bool skipped = false, string? skipReason = null)
        => PerfHarnessResults.Operation(name, wallMs, peakMb, failed, skipped, skipReason);

    private static JsonObject Op16(string name, double wallMs = 300, double peakMb = 150, bool skipped = false, string? skipReason = null)
        => PerfHarnessResults.Operation(name, wallMs, peakMb, skipped: skipped, skipReason: skipReason);
}
