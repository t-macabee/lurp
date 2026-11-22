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
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(0, code);
        Assert.Contains("gate verdict: PASS", stdout);
    }

    [Fact]
    public void TimeRatioSixOnFullIndex_Fails()
    {
        var ops16 = ComparedOperations.Select(name => name == "full_index" ? Op16(name, 600) : Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16, size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void TimeRatioSixOnSearch_PassesWithWarn()
    {
        var ops16 = ComparedOperations.Select(name => name == "search" ? Op16(name, 600) : Op16(name));
        var (code, stdout, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16, size: 16));

        Assert.Equal(0, code);
        Assert.Contains("WARN", stdout);
    }

    [Fact]
    public void MemoryRatioSixOnSearch_Fails()
    {
        var ops16 = ComparedOperations.Select(name => name == "search" ? Op16(name, 300, 300) : Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16, size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void OperationMissingAt16x_Fails()
    {
        var ops16 = ComparedOperations.Where(name => name != "search").Select(name => Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16, size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void OperationMissingAt4x_Fails()
    {
        var ops4 = ComparedOperations.Where(name => name != "search").Select(name => Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Theory]
    [InlineData("full_index")]
    [InlineData("incremental_1_file")]
    [InlineData("incremental_20_files")]
    [InlineData("diff")]
    [InlineData("dead_candidates")]
    public void RequiredOperationMissingInBoth_Fails(string missing)
    {
        var ops4 = ComparedOperations.Where(name => name != missing).Select(name => Op4(name));
        var ops16 = ComparedOperations.Where(name => name != missing).Select(name => Op16(name));
        var (code, stdout, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ops16, size: 16));

        Assert.Equal(1, code);
        Assert.Contains($"| {missing} | - | - | - | - | - | - | FAIL (missing in both results) |", stdout);
    }

    [Fact]
    public void FailedOperation_Fails()
    {
        var ops4 = ComparedOperations.Select(name => name == "full_index" ? Op4(name, failed: true) : Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void ProtocolNotGate_Fails()
    {
        var (code, _, _) = RunEvaluate(
            Result("custom", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void RunsDiffer_Fails()
    {
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 6, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void WarmupDiffers_Fails()
    {
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 2, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void UnreadableJson_Returns2()
    {
        var path4 = Path.Combine(_tempDir, "4x.json");
        File.WriteAllText(path4, "{not json");
        var path16 = PerfHarnessResults.WriteResult(_tempDir, "16x.json", Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        var (code, _, _) = PerfHarnessResults.RunHarness([$"--evaluate={path4},{path16}"]);

        Assert.Equal(2, code);
    }

    [Fact]
    public void MaxRatioThreeMakesRatioFourFail()
    {
        var ops16 = ComparedOperations.Select(name => name == "full_index" ? Op16(name, 400) : Op16(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16, size: 16),
            "--max-ratio=3");

        Assert.Equal(1, code);
    }

    [Fact]
    public void RatioExactlyAtLimit_Passes()
    {
        var ops16 = ComparedOperations.Select(name => Op16(name, 500, 250));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))),
            Result("gate", 5, 3, ops16, size: 16));

        Assert.Equal(0, code);
    }

    [Fact]
    public void PeakBytes_UseTheExactCountsAtTheGrowthLimit()
    {
        var small = StandardResult(4);
        var large = StandardResult(16);
        PerfHarnessResults.WithPeakBytes(PerfHarnessResults.FindOperation(small, "full_index"), 100 * PerfHarnessResults.MiB);
        PerfHarnessResults.WithPeakBytes(PerfHarnessResults.FindOperation(large, "full_index"), 300 * PerfHarnessResults.MiB);
        var (atBoundary, _, _) = RunEvaluate(small, large, "--max-ratio=3");
        Assert.Equal(0, atBoundary);

        PerfHarnessResults.WithPeakBytes(PerfHarnessResults.FindOperation(large, "full_index"), (300 * PerfHarnessResults.MiB) + 1);
        var (aboveBoundary, stdout, _) = RunEvaluate(small, large, "--max-ratio=3");
        Assert.Equal(1, aboveBoundary);
        Assert.Contains("memory ratio 3.0000 above limit 3", Row(stdout, "full_index"));
    }

    [Fact]
    public void LegacyWholeMiBResults_DoNotCertifyANearLimitGrowthPass()
    {
        var small = PerfHarnessResults.ToLegacy(StandardResult(4));
        var large = PerfHarnessResults.ToLegacy(StandardResult(16));
        var (code, stdout, _) = RunEvaluate(small, large, "--max-ratio=3");

        Assert.Equal(1, code);
        Assert.Contains("cannot be proven at or below limit 3", Row(stdout, "full_index"));
        Assert.Contains("~", Row(stdout, "full_index"));
    }

    [Fact]
    public void MedianZero_Fails()
    {
        var ops4 = ComparedOperations.Select(name => name == "full_index" ? Op4(name, 0) : Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("16x")]
    public void FixtureSkip_Fails(string side)
    {
        var small = StandardResult(4);
        var large = StandardResult(16);
        foreach (var result in side == "both" ? new[] { small, large } : [large])
        {
            var op = PerfHarnessResults.FindOperation(result, "full_index");
            op["skipped"] = true;
            op["skip_reason"] = "no anchors available";
        }

        var (code, stdout, _) = RunEvaluate(small, large);
        Assert.Equal(1, code);
        Assert.Contains($"skipped in {(side == "both" ? "4x" : "16x")} result", stdout);
    }

    [Theory]
    [InlineData("swapped", "fixture.size")]
    [InlineData("same size", "fixture.size")]
    [InlineData("generator version", "fixture.generator_version")]
    [InlineData("seed", "fixture.seed")]
    [InlineData("solution", "fixture is missing")]
    [InlineData("protocol counts", "expected 5")]
    public void InvalidFiles_Fail(string change, string reason)
    {
        var small = StandardResult(4);
        var large = StandardResult(16);
        switch (change)
        {
            case "swapped": (small, large) = (large, small); break;
            case "same size": large["fixture"]!["size"] = 4; break;
            case "generator version": large["fixture"]!["generator_version"] = "1.0.0"; break;
            case "seed": large["fixture"]!["seed"] = 2; break;
            case "solution":
                foreach (var result in new[] { small, large })
                {
                    result["fixture"] = null;
                    result["solution"] = "Perf.slnx";
                }
                break;
            case "protocol counts":
                foreach (var result in new[] { small, large })
                {
                    result["runs"] = 1;
                    result["warmup"] = 0;
                    foreach (var operation in result["operations"]!.AsArray())
                        operation!["runs"] = new JsonArray(operation["runs"]![0]!.DeepClone());
                }
                break;
        }

        var (code, stdout, _) = RunEvaluate(small, large);
        Assert.Equal(1, code);
        Assert.Contains("gate failure:", stdout);
        Assert.Contains(reason, stdout);
    }

    [Theory]
    [InlineData("median", "4x result: stored median wall_ms")]
    [InlineData("exit", "16x result: run 1 has exit code 1")]
    [InlineData("count", "4x result: 4 run records, expected 5")]
    public void InvalidRunRecords_Fail(string change, string reason)
    {
        var small = StandardResult(4);
        var large = StandardResult(16);
        var runs = PerfHarnessResults.FindOperation(change == "exit" ? large : small, "full_index")["runs"]!.AsArray();
        switch (change)
        {
            case "median":
                foreach (var run in runs)
                    run!["wall_ms"] = 900;
                break;
            case "exit": runs[0]!["exit_code"] = 1; break;
            case "count": runs.RemoveAt(0); break;
        }
        var (code, stdout, _) = RunEvaluate(small, large);

        Assert.Equal(1, code);
        Assert.Contains(reason, stdout);
    }

    [Fact]
    public void FailingRatio_PrintsFourDecimals()
    {
        var small = StandardResult(4);
        var large = StandardResult(16);
        small["operations"]![0] = Op4("full_index", 1000);
        large["operations"]![0] = Op16("full_index", 5004);

        var (code, stdout, _) = RunEvaluate(small, large);

        Assert.Equal(1, code);
        Assert.Contains("time ratio 5.0040 above limit 5", stdout);
    }

    [Theory]
    [InlineData(4, "zero", "median is 0")]
    [InlineData(16, "zero", "median is 0")]
    [InlineData(4, "null", "median missing or null")]
    [InlineData(16, "null", "median missing or null")]
    [InlineData(4, "failed", "failed in")]
    [InlineData(16, "failed", "failed in")]
    public void InvalidOperation_Fails(int size, string change, string reason)
    {
        var small = StandardResult(4);
        var large = StandardResult(16);
        var operation = PerfHarnessResults.FindOperation(size == 4 ? small : large, "full_index");
        PerfHarnessResults.InvalidateOperation(operation, change);

        var (code, stdout, _) = RunEvaluate(small, large);

        Assert.Equal(1, code);
        Assert.Contains($"{reason} {(change == "failed" ? "" : "in ")}{size}x result", stdout);
    }

    [Fact]
    public void SummaryUnderAFile_Returns2()
    {
        var file = Path.Combine(_tempDir, "file");
        File.WriteAllText(file, "");

        var (code, _, stderr) = RunEvaluate(StandardResult(4), StandardResult(16), $"--summary={Path.Combine(file, "summary.md")}");

        Assert.Equal(2, code);
        Assert.DoesNotContain("unexpected", stderr);
    }

    [Fact]
    public void UndocumentedSkip_Fails()
    {
        var ops4 = ComparedOperations.Select(name => name == "search"
            ? Op4(name, skipped: true, skipReason: "flaky")
            : Op4(name));
        var (code, _, _) = RunEvaluate(
            Result("gate", 5, 3, ops4),
            Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));

        Assert.Equal(1, code);
    }

    [Fact]
    public void SummaryFile_MatchesStdout()
    {
        var path4 = PerfHarnessResults.WriteResult(_tempDir, "4x.json", Result("gate", 5, 3, ComparedOperations.Select(name => Op4(name))));
        var path16 = PerfHarnessResults.WriteResult(_tempDir, "16x.json", Result("gate", 5, 3, ComparedOperations.Select(name => Op16(name)), size: 16));
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

    private static JsonObject StandardResult(int size)
        => Result("gate", 5, 3, ComparedOperations.Select(name => size == 4 ? Op4(name) : Op16(name)), size);

    private static JsonObject Result(string protocol, int runs, int warmup, IEnumerable<JsonObject> operations, int size = 4)
    {
        var array = new JsonArray();
        foreach (var operation in operations)
            array.Add(operation);
        return new JsonObject
        {
            ["schema_version"] = 2,
            ["protocol"] = protocol,
            ["runs"] = runs,
            ["warmup"] = warmup,
            ["fixture"] = new JsonObject { ["size"] = size, ["generator_version"] = "1.1.0", ["seed"] = 1 },
            ["solution"] = null,
            ["operations"] = array
        };
    }

    // Base medians: 4x = 100 ms / 50 MB, 16x = 300 ms / 150 MB, so every ratio is 3.
    private static JsonObject Op4(string name, double wallMs = 100, double peakMb = 50, bool failed = false, bool skipped = false, string? skipReason = null)
        => PerfHarnessResults.Operation(name, wallMs, peakMb, failed, skipped, skipReason);

    private static JsonObject Op16(string name, double wallMs = 300, double peakMb = 150, bool skipped = false, string? skipReason = null)
        => PerfHarnessResults.Operation(name, wallMs, peakMb, skipped: skipped, skipReason: skipReason);

    private static string Row(string stdout, string name)
        => stdout.Split('\n').First(line => line.StartsWith($"| {name} |", StringComparison.Ordinal));
}
