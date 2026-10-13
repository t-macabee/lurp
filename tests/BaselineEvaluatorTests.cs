using System.Text.Json.Nodes;

namespace Lurp.Tests;

/// <summary>
///     Contract tests for the baseline perf gate evaluator (scripts/perf/harness/BaselineEvaluator.cs).
///     Each test builds a synthetic baseline and candidate result file, runs the real harness
///     as a process, and asserts the exit code and the report text.
/// </summary>
public sealed class BaselineEvaluatorTests : IDisposable
{
    private const string Commit = "4ba9901a527b12978d3d86996026ce16fe2475e1";
    private const string NeverEdits = "--solution runs never edit files";

    private static readonly string[] DefaultOperationNames =
    [
        "full_index", "noop_incremental", "impact.upstream.d3.middle", "impact.upstream.d6.middle",
        "context.default.middle", "search", "grep", "find_symbol", "diff", "dead_candidates",
        "mcp_status.full"
    ];

    private readonly string _tempDir;

    public BaselineEvaluatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lurp-baseline-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_tempDir);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void SameResult_Passes()
    {
        var (code, stdout, _) = RunBaseline(Result(), Result());

        Assert.Equal(0, code);
        Assert.Contains("gate verdict: PASS", stdout);
    }

    [Fact]
    public void MemoryAboveFactor_Fails()
    {
        var ops = DefaultOperations();
        With(Find(ops, "full_index"), peakMb: 126);
        var (code, stdout, _) = RunBaseline(Result(), Result(operations: ops));

        Assert.Equal(1, code);
        Assert.Contains("FAIL", Row(stdout, "full_index"));
    }

    [Fact]
    public void MemoryAtFactor_Passes()
    {
        var ops = DefaultOperations();
        With(Find(ops, "full_index"), peakMb: 125);
        var (code, _, _) = RunBaseline(Result(), Result(operations: ops));

        Assert.Equal(0, code);
    }

    [Fact]
    public void InteractiveReadAboveCap_FailsEvenWhenBaselineIsEqual()
    {
        var baselineOps = DefaultOperations();
        With(Find(baselineOps, "search"), peakMb: 310);
        var candidateOps = DefaultOperations();
        With(Find(candidateOps, "search"), peakMb: 310);
        var (code, _, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(1, code);
    }

    [Fact]
    public void IndexAboveCap_Passes()
    {
        var baselineOps = DefaultOperations();
        With(Find(baselineOps, "full_index"), peakMb: 435);
        var candidateOps = DefaultOperations();
        With(Find(candidateOps, "full_index"), peakMb: 435);
        var (code, _, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(0, code);
    }

    [Fact]
    public void TimeAboveFactor_Warns()
    {
        var ops = DefaultOperations();
        With(Find(ops, "diff"), wallMs: 1300);
        var (code, stdout, _) = RunBaseline(Result(), Result(operations: ops));

        Assert.Equal(0, code);
        Assert.Contains("WARN", Row(stdout, "diff"));
    }

    [Fact]
    public void ImpactDepth6AboveCap_Warns()
    {
        var baselineOps = DefaultOperations();
        With(Find(baselineOps, "impact.upstream.d6.middle"), wallMs: 2100);
        var candidateOps = DefaultOperations();
        With(Find(candidateOps, "impact.upstream.d6.middle"), wallMs: 2100);
        var (code, stdout, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(0, code);
        Assert.Contains("WARN", Row(stdout, "impact.upstream.d6.middle"));
    }

    [Fact]
    public void ImpactDepth3AboveDepth6Cap_Passes()
    {
        var baselineOps = DefaultOperations();
        With(Find(baselineOps, "impact.upstream.d3.middle"), wallMs: 2100);
        var candidateOps = DefaultOperations();
        With(Find(candidateOps, "impact.upstream.d3.middle"), wallMs: 2100);
        var (code, stdout, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(0, code);
        Assert.Contains("| PASS |", Row(stdout, "impact.upstream.d3.middle"));
    }

    [Fact]
    public void ContextAboveCap_Warns()
    {
        var baselineOps = DefaultOperations();
        With(Find(baselineOps, "context.default.middle"), wallMs: 3100);
        var candidateOps = DefaultOperations();
        With(Find(candidateOps, "context.default.middle"), wallMs: 3100);
        var (code, stdout, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(0, code);
        Assert.Contains("WARN", Row(stdout, "context.default.middle"));
    }

    [Fact]
    public void SearchAboveCap_Warns()
    {
        var baselineOps = DefaultOperations();
        With(Find(baselineOps, "search"), wallMs: 600);
        var candidateOps = DefaultOperations();
        With(Find(candidateOps, "search"), wallMs: 600);
        var (code, stdout, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(0, code);
        Assert.Contains("WARN", Row(stdout, "search"));
    }

    [Fact]
    public void MissingInCandidate_Fails()
    {
        var candidateOps = DefaultOperations().Where(op => (string?)op["name"] != "grep").ToList();
        var (code, _, _) = RunBaseline(Result(), Result(operations: candidateOps));

        Assert.Equal(1, code);
    }

    [Fact]
    public void MissingInBaseline_Fails()
    {
        var baselineOps = DefaultOperations().Where(op => (string?)op["name"] != "grep").ToList();
        var (code, _, _) = RunBaseline(Result(operations: baselineOps), Result());

        Assert.Equal(1, code);
    }

    [Fact]
    public void AnchorSkip_Fails()
    {
        var baselineOps = DefaultOperations();
        WithSkip(Find(baselineOps, "search"), "no anchors available");
        var candidateOps = DefaultOperations();
        WithSkip(Find(candidateOps, "search"), "no anchors available");
        var (code, _, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));

        Assert.Equal(1, code);
    }

    [Fact]
    public void EditSkip_IsListed()
    {
        var (code, stdout, _) = RunBaseline(Result(), Result());

        Assert.Equal(0, code);
        Assert.Contains("skipped as solution-only (not compared): incremental_1_file, incremental_20_files", stdout);
    }

    [Fact]
    public void RequiredOperationMissingInBoth_Fails()
    {
        var baselineOps = DefaultOperations().Where(op => (string?)op["name"] != "dead_candidates").ToList();
        var candidateOps = DefaultOperations().Where(op => (string?)op["name"] != "dead_candidates").ToList();
        var (code1, _, _) = RunBaseline(Result(operations: baselineOps), Result(operations: candidateOps));
        Assert.Equal(1, code1);

        var baselineImpact = DefaultOperations().Where(op => !((string?)op["name"] ?? "").StartsWith("impact.", StringComparison.Ordinal)).ToList();
        var candidateImpact = DefaultOperations().Where(op => !((string?)op["name"] ?? "").StartsWith("impact.", StringComparison.Ordinal)).ToList();
        var (code2, _, _) = RunBaseline(Result(operations: baselineImpact), Result(operations: candidateImpact));
        Assert.Equal(1, code2);
    }

    [Fact]
    public void SolutionCommitDiffers_Fails()
    {
        var (code, _, _) = RunBaseline(Result(), Result(solutionCommit: "0000000000000000000000000000000000000000"));

        Assert.Equal(1, code);
    }

    [Fact]
    public void SolutionCommitMissing_Fails()
    {
        var (code, _, _) = RunBaseline(Result(solutionCommit: null), Result(solutionCommit: null));

        Assert.Equal(1, code);
    }

    [Fact]
    public void RunnerImageDiffers_Warns()
    {
        var (code, stdout, _) = RunBaseline(Result(), Result(runnerImage: "20261012.1.0"));

        Assert.Equal(0, code);
        Assert.Contains("warning: runner image differs", stdout);
    }

    [Fact]
    public void ProtocolNotGate_Fails()
    {
        var (code, _, _) = RunBaseline(Result(), Result(protocol: "custom"));

        Assert.Equal(1, code);
    }

    [Fact]
    public void RunsDiffer_Fails()
    {
        var (code, _, _) = RunBaseline(Result(), Result(runs: 6));

        Assert.Equal(1, code);
    }

    [Fact]
    public void UnreadableJson_Returns2()
    {
        var pathBaseline = Path.Combine(_tempDir, "baseline.json");
        File.WriteAllText(pathBaseline, "{not json");
        var pathCandidate = PerfHarnessResults.WriteResult(_tempDir, "candidate.json", Result());

        var (code, _, _) = PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}", $"--candidate={pathCandidate}"]);

        Assert.Equal(2, code);
    }

    [Fact]
    public void BaselineOptions_Misused_Return2()
    {
        var pathBaseline = PerfHarnessResults.WriteResult(_tempDir, "baseline.json", Result());
        var pathCandidate = PerfHarnessResults.WriteResult(_tempDir, "candidate.json", Result());

        var (code1, _, _) = PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}"]);
        Assert.Equal(2, code1);

        var (code2, _, _) = PerfHarnessResults.RunHarness([$"--candidate={pathCandidate}"]);
        Assert.Equal(2, code2);

        var (code3, _, _) = PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}", "--evaluate=a.json,b.json"]);
        Assert.Equal(2, code3);

        var (code4, _, _) = PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}", $"--candidate={pathCandidate}", "--max-ratio=3"]);
        Assert.Equal(2, code4);

        var (code5, _, _) = PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}", $"--candidate={pathCandidate}", "--work=x"]);
        Assert.Equal(2, code5);
    }

    [Fact]
    public void SummaryFile_MatchesStdout()
    {
        var pathBaseline = PerfHarnessResults.WriteResult(_tempDir, "baseline.json", Result());
        var pathCandidate = PerfHarnessResults.WriteResult(_tempDir, "candidate.json", Result());
        var summaryPath = Path.Combine(_tempDir, "baseline-summary.md");

        var (code, stdout, _) = PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}", $"--candidate={pathCandidate}", $"--summary={summaryPath}"]);

        Assert.Equal(0, code);
        var summary = File.ReadAllText(summaryPath);
        Assert.True(stdout.StartsWith(summary, StringComparison.Ordinal),
            "stdout should start with the summary markdown");
    }

    private (int Code, string Stdout, string Stderr) RunBaseline(JsonObject baseline, JsonObject candidate, params string[] extraArgs)
    {
        var pathBaseline = PerfHarnessResults.WriteResult(_tempDir, "baseline.json", baseline);
        var pathCandidate = PerfHarnessResults.WriteResult(_tempDir, "candidate.json", candidate);
        return PerfHarnessResults.RunHarness([$"--baseline={pathBaseline}", $"--candidate={pathCandidate}", .. extraArgs]);
    }

    private static JsonObject Result(
        string? protocol = "gate",
        int runs = 5,
        int warmup = 3,
        string? solution = @"C:\x\eNote.sln",
        string? solutionCommit = Commit,
        string? runnerOs = "win25",
        string? runnerImage = "20261005.1.0",
        IEnumerable<JsonObject>? operations = null)
    {
        var array = new JsonArray();
        foreach (var operation in operations ?? DefaultOperations())
            array.Add(operation);
        return new JsonObject
        {
            ["schema_version"] = 1,
            ["protocol"] = protocol,
            ["runs"] = runs,
            ["warmup"] = warmup,
            ["solution"] = solution,
            ["solution_commit"] = solutionCommit,
            ["runner_os"] = runnerOs,
            ["runner_image"] = runnerImage,
            ["operations"] = array
        };
    }

    private static List<JsonObject> DefaultOperations()
    {
        var ops = DefaultOperationNames
            .Select(name => PerfHarnessResults.Operation(name, 1000, 100))
            .ToList();
        ops.Add(PerfHarnessResults.Operation("incremental_1_file", 0, 0, skipped: true, skipReason: NeverEdits));
        ops.Add(PerfHarnessResults.Operation("incremental_20_files", 0, 0, skipped: true, skipReason: NeverEdits));
        return ops;
    }

    private static JsonObject Find(IEnumerable<JsonObject> ops, string name)
        => ops.First(op => (string?)op["name"] == name);

    private static JsonObject With(JsonObject op, double? wallMs = null, double? peakMb = null)
    {
        var median = (JsonObject)op["median"]!;
        if (wallMs != null)
            median["wall_ms"] = wallMs.Value;
        if (peakMb != null)
            median["peak_working_set_mb"] = peakMb.Value;
        return op;
    }

    private static JsonObject WithSkip(JsonObject op, string reason)
    {
        op["skipped"] = true;
        op["skip_reason"] = reason;
        op["median"] = null;
        return op;
    }

    private static string Row(string stdout, string name)
        => stdout.Split('\n').First(line => line.StartsWith($"| {name} |", StringComparison.Ordinal));
}
