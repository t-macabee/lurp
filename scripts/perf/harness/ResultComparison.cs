using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal sealed record GateResult(string Label, JsonObject Root, Dictionary<string, JsonObject> Operations)
{
    public static GateResult Read(string path, string label)
    {
        var root = ResultFile.ReadResult(path);
        return new GateResult(label, root, ResultFile.ReadOperations(path, root));
    }
}

internal readonly record struct Measurements(double Wall, double Peak);

internal readonly record struct ComparedOperation(
    string Name, string? Failure = null, bool Excluded = false,
    Measurements First = default, Measurements Second = default)
{
    public double TimeRatio => Second.Wall / First.Wall;
    public double MemoryRatio => Second.Peak / First.Peak;

    public string FormatRow(string verdict)
        => $"| {Name} | {ResultFile.Format(First.Wall)} | {ResultFile.Format(Second.Wall)} | {ResultFile.FormatRatio(TimeRatio)} | {ResultFile.Format(First.Peak)} | {ResultFile.Format(Second.Peak)} | {ResultFile.FormatRatio(MemoryRatio)} | {verdict} |";
}

internal sealed class ResultComparison
{
    private readonly List<string> _rows = [];
    private readonly List<string> _excluded = [];
    private int _failedRows;
    private int _warningRows;

    public List<string> FileFailures { get; } = [];
    public IReadOnlyList<ComparedOperation> Operations { get; }

    public ResultComparison(GateResult first, GateResult second, string[] required, Func<string, bool>? acceptsSkip = null)
    {
        ValidateProtocol(first);
        ValidateProtocol(second);
        Operations = first.Operations.Keys.Union(second.Operations.Keys).Union(required)
            .Order(StringComparer.Ordinal)
            .Select(name => CompareOperation(first, second, name, acceptsSkip))
            .ToArray();

        foreach (var op in Operations.Where(op => op.Excluded))
            _excluded.Add(op.Name);
    }

    private void ValidateProtocol(GateResult result)
    {
        var protocol = JsonTools.GetString(result.Root, "protocol");
        if (protocol != GateProtocol.Name)
            FileFailures.Add($"{result.Label} result: protocol is '{protocol ?? "null"}', expected '{GateProtocol.Name}'");

        var runs = JsonTools.GetInt(result.Root, "runs");
        if (runs != GateProtocol.Runs)
            FileFailures.Add($"{result.Label} result: runs is {runs?.ToString(CultureInfo.InvariantCulture) ?? "missing or invalid"}, expected {GateProtocol.Runs}");

        var warmup = JsonTools.GetInt(result.Root, "warmup");
        if (warmup != GateProtocol.Warmup)
            FileFailures.Add($"{result.Label} result: warmup is {warmup?.ToString(CultureInfo.InvariantCulture) ?? "missing or invalid"}, expected {GateProtocol.Warmup}");
    }

    private static ComparedOperation CompareOperation(
        GateResult first, GateResult second, string name, Func<string, bool>? acceptsSkip)
    {
        first.Operations.TryGetValue(name, out var left);
        second.Operations.TryGetValue(name, out var right);
        if (left == null && right == null)
            return new(name, "missing in both results");
        if (left == null)
            return new(name, $"missing in {first.Label} result");
        if (right == null)
            return new(name, $"missing in {second.Label} result");
        if (ResultFile.ReadFailed(left))
            return new(name, $"failed in {first.Label} result");
        if (ResultFile.ReadFailed(right))
            return new(name, $"failed in {second.Label} result");

        var leftSkipped = ResultFile.ReadSkipped(left, out var leftReason);
        var rightSkipped = ResultFile.ReadSkipped(right, out var rightReason);
        if (leftSkipped && rightSkipped && acceptsSkip != null && acceptsSkip(leftReason) && acceptsSkip(rightReason))
            return new(name, Excluded: true);
        var only = acceptsSkip != null && leftSkipped != rightSkipped ? " only" : "";
        if (leftSkipped && (!rightSkipped || acceptsSkip == null || !acceptsSkip(leftReason)))
            return new(name, $"skipped in {first.Label} result{only}: {leftReason}");
        if (rightSkipped)
            return new(name, $"skipped in {second.Label} result{only}: {rightReason}");

        var failure = ValidateRuns(left, first.Label, out var firstMeasurements);
        if (failure != null)
            return new(name, failure);
        failure = ValidateRuns(right, second.Label, out var secondMeasurements);
        if (failure != null)
            return new(name, failure);
        if (firstMeasurements.Wall <= 0 || firstMeasurements.Peak <= 0)
            return new(name, $"median is {(firstMeasurements.Wall < 0 || firstMeasurements.Peak < 0 ? "negative" : "0")} in {first.Label} result");
        if (secondMeasurements.Wall <= 0 || secondMeasurements.Peak <= 0)
            return new(name, $"median is {(secondMeasurements.Wall < 0 || secondMeasurements.Peak < 0 ? "negative" : "0")} in {second.Label} result");
        return new(name, First: firstMeasurements, Second: secondMeasurements);
    }

    private static string? ValidateRuns(JsonObject operation, string label, out Measurements measurements)
    {
        measurements = default;
        var side = $"{label} result";
        if (operation["runs"] is not JsonArray runs)
            return $"{side}: runs array is missing or invalid";
        if (runs.Count != GateProtocol.Runs)
            return $"{side}: {runs.Count} run records, expected {GateProtocol.Runs}";

        var walls = new double[runs.Count];
        var peaks = new double[runs.Count];
        for (var i = 0; i < runs.Count; i++)
        {
            if (runs[i] is not JsonObject run)
                return $"{side}: run {i + 1} is not an object";
            var exitCode = JsonTools.GetInt(run, "exit_code");
            if (exitCode != 0)
                return $"{side}: run {i + 1} has exit code {exitCode?.ToString(CultureInfo.InvariantCulture) ?? "missing or invalid"}";
            if (!ResultFile.TryReadMeasurements(run, out walls[i], out peaks[i]))
                return $"{side}: run {i + 1} lacks numeric wall_ms or peak_working_set_mb";
        }

        if (!ResultFile.TryReadMedian(operation, out var wall, out var peak))
            return $"median missing or null in {side}";
        var wallMedian = ResultFile.Median(walls);
        var peakMedian = ResultFile.Median(peaks);
        if (wall != wallMedian)
            return FormattableString.Invariant($"{side}: stored median wall_ms {wall} is not the median of the runs ({wallMedian})");
        if (peak != peakMedian)
            return FormattableString.Invariant($"{side}: stored median peak_working_set_mb {peak} is not the median of the runs ({peakMedian})");
        measurements = new Measurements(wall, peak);
        return null;
    }

    public void AddRow(ComparedOperation operation, IReadOnlyList<string> failures, IReadOnlyList<string> warnings)
    {
        if (operation.Excluded)
            return;
        if (operation.Failure is { } validationFailure)
        {
            _rows.Add($"| {operation.Name} | - | - | - | - | - | - | FAIL ({validationFailure}) |");
            _failedRows++;
            return;
        }

        var verdict = "PASS";
        if (warnings.Count > 0)
        {
            _warningRows++;
            verdict = $"WARN ({string.Join("; ", warnings)})";
        }
        if (failures.Count > 0)
        {
            _failedRows++;
            verdict = $"FAIL ({string.Join("; ", failures.Concat(warnings))})";
        }
        _rows.Add(operation.FormatRow(verdict));
    }

    public int WriteReport(string header, string? summary, IReadOnlyList<string>? details = null, int reportWarnings = 0)
    {
        var report = new StringBuilder();
        foreach (var failure in FileFailures)
            report.AppendLine(CultureInfo.InvariantCulture, $"gate failure: {failure}");
        if (FileFailures.Count > 0)
            report.AppendLine();
        if (details is { Count: > 0 })
        {
            foreach (var line in details)
                report.AppendLine(line);
            report.AppendLine();
        }
        report.AppendLine(header);
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var row in _rows)
            report.AppendLine(row);
        if (_excluded.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"skipped as solution-only (not compared): {string.Join(", ", _excluded)}");
        }

        report.AppendLine();
        var failures = _failedRows + FileFailures.Count;
        var verdict = failures > 0 ? "FAIL" : "PASS";
        report.AppendLine(CultureInfo.InvariantCulture, $"gate verdict: {verdict} ({failures} failure(s), {_warningRows + reportWarnings} warning(s))");
        ResultFile.WriteReport(report.ToString(), summary);
        return failures > 0 ? 1 : 0;
    }
}
