using System.Globalization;
using System.Text.RegularExpressions;

namespace Lurp.PerfHarness;

/// <summary>
///     The baseline perf gate: compares a candidate result file against a committed baseline
///     result file and fails on a memory regression or an over-cap interactive read.
/// </summary>
internal static class BaselineEvaluator
{
    // Candidate peak above this factor of the baseline peak fails, for every operation.
    private const double MemoryFactor = 1.25;

    // Warn on a time ratio only when the baseline reaches the noise floor.
    private const double TimeFactor = 1.25;
    private const double TimeNoiseFloorMs = 25;

    // Peak working set above this fails for an interactive read (impact, context, search, grep, find_symbol only).
    private const double InteractiveReadPeakCapMb = 300;
    private const double InteractiveReadPeakCapBytes = InteractiveReadPeakCapMb * ResultFile.BytesPerMiB;

    // Median wall above this warns for an impact operation at depth 6 (the audit's interactive cap).
    private const double ImpactDepth6CapMs = 2000;

    // Median wall above this warns for a context operation (the audit's interactive cap).
    private const double ContextCapMs = 3000;

    // Median wall above this warns for search (the audit's interactive cap).
    private const double SearchCapMs = 500;

    // Operations that must be present and measured in both files.
    private static readonly string[] RequiredOperations =
    [
        "full_index",
        "noop_incremental",
        "search",
        "grep",
        "find_symbol",
        "dead_candidates"
    ];

    // --solution runs skip edit and touch operations for these reasons; they carry no measurement.
    private static readonly string[] AllowedSkipReasons =
    [
        SkipReasons.NeverEdits,
        SkipReasons.NeverTouches
    ];

    private static readonly Regex ToolVersionPattern = new(@"^lurp ([0-9]+)\.([0-9]+)\.", RegexOptions.CultureInvariant);

    public static int Evaluate(HarnessOptions options)
    {
        var baseline = GateResult.Read(options.Baseline!, "baseline");
        var candidate = GateResult.Read(options.Candidate!, "candidate");
        var selfCheck = string.Equals(
            Path.GetFullPath(options.Baseline!), Path.GetFullPath(options.Candidate!), StringComparison.Ordinal);
        var comparison = new ResultComparison(baseline, candidate, RequiredOperations, AllowedSkipReasons.Contains);

        if (selfCheck)
            comparison.FileFailures.Add(
                "baseline and candidate are the same file: the harness checked the candidate against itself, not against a committed baseline; the release gate failed");

        foreach (var result in new[] { baseline, candidate })
        {
            if (JsonTools.GetString(result.Root, "solution") == null)
                comparison.FileFailures.Add($"{result.Label} result: solution is missing or invalid");
        }

        var baselineCommit = JsonTools.GetString(baseline.Root, "solution_commit");
        var candidateCommit = JsonTools.GetString(candidate.Root, "solution_commit");
        if (string.IsNullOrEmpty(baselineCommit) || string.IsNullOrEmpty(candidateCommit))
            comparison.FileFailures.Add($"solution_commit is missing: baseline '{baselineCommit ?? "null"}', candidate '{candidateCommit ?? "null"}'");
        else if (!string.Equals(baselineCommit, candidateCommit, StringComparison.Ordinal))
            comparison.FileFailures.Add($"solution_commit differs: baseline '{baselineCommit}', candidate '{candidateCommit}'");

        var baselineOs = JsonTools.GetString(baseline.Root, "runner_os");
        var candidateOs = JsonTools.GetString(candidate.Root, "runner_os");
        if (!string.Equals(baselineOs, candidateOs, StringComparison.Ordinal))
            comparison.FileFailures.Add($"runner_os differs: baseline '{baselineOs ?? "null"}', candidate '{candidateOs ?? "null"}'");

        var baselineVersion = JsonTools.GetString(baseline.Root, "tool_version") ?? "null";
        var candidateVersion = JsonTools.GetString(candidate.Root, "tool_version") ?? "null";
        var details = new List<string> { $"tool versions: baseline {baselineVersion}, candidate {candidateVersion}" };
        AddVersionWarnings(details, baselineVersion, candidateVersion);
        AddPrecisionWarnings(details, baseline, candidate);

        var baselineImage = JsonTools.GetString(baseline.Root, "runner_image");
        var candidateImage = JsonTools.GetString(candidate.Root, "runner_image");
        if (!string.Equals(baselineImage, candidateImage, StringComparison.Ordinal))
            details.Add($"warning: runner image differs: baseline {baselineImage ?? "null"}, candidate {candidateImage ?? "null"}");

        var hasMeasuredImpact = false;
        var hasMeasuredContext = false;
        foreach (var operation in comparison.Operations)
        {
            if (operation.Excluded || operation.Failure != null)
            {
                comparison.AddRow(operation, [], []);
                continue;
            }

            var name = operation.Name;
            hasMeasuredImpact |= name.StartsWith("impact.", StringComparison.Ordinal);
            hasMeasuredContext |= IsContext(name);

            var failures = new List<string>();
            var warnings = new List<string>();
            var wall = operation.Second.Wall;
            var peak = operation.Second.Peak;
            if (!operation.MemoryExact)
                warnings.Add("memory values are approximate: a result stores whole MiB only");
            if (operation.MemoryRatioMin > MemoryFactor)
                failures.Add(ResultFile.FormatRatioReason("memory", operation.MemoryRatioMin, MemoryFactor));
            else if (operation.MemoryRatioMax > MemoryFactor)
                failures.Add(ResultFile.FormatPrecisionReason("memory", operation.MemoryRatioMin, operation.MemoryRatioMax, MemoryFactor));
            if (IsInteractiveRead(name) && peak.MaxBytes > InteractiveReadPeakCapBytes)
                failures.Add(FormatCap("interactive read peak", peak.MaxBytes / ResultFile.BytesPerMiB, InteractiveReadPeakCapMb, "MB"));
            if (operation.First.Wall >= TimeNoiseFloorMs && operation.TimeRatio > TimeFactor)
                warnings.Add(ResultFile.FormatRatioReason("time", operation.TimeRatio, TimeFactor));
            if (IsImpactDepth6(name) && wall > ImpactDepth6CapMs)
                warnings.Add(FormatCap("impact depth 6", wall, ImpactDepth6CapMs, "ms"));
            if (IsContext(name) && wall > ContextCapMs)
                warnings.Add(FormatCap("context", wall, ContextCapMs, "ms"));
            if (name == "search" && wall > SearchCapMs)
                warnings.Add(FormatCap("search", wall, SearchCapMs, "ms"));
            comparison.AddRow(operation, failures, warnings);
        }

        if (!hasMeasuredImpact)
            comparison.FileFailures.Add("no measured impact.* operation");
        if (!hasMeasuredContext)
            comparison.FileFailures.Add("no measured context.* operation");

        return comparison.WriteReport(
            "| operation | time baseline | time candidate | time ratio | memory baseline MiB | memory candidate MiB | memory ratio | verdict |",
            options.Summary, details, reportWarnings: details.Count - 1);
    }

    private static void AddPrecisionWarnings(List<string> details, GateResult baseline, GateResult candidate)
    {
        if (baseline.SchemaVersion < 2)
            details.Add(
                "warning: baseline result stores peak_working_set_mb only (whole MiB); its memory values are approximate. Refresh the baseline with peak_working_set_bytes.");
        if (candidate.SchemaVersion < 2)
            details.Add(
                "warning: candidate result stores peak_working_set_mb only (whole MiB); its memory values are approximate. Regenerate the candidate with the current harness to record peak_working_set_bytes.");
    }

    private static void AddVersionWarnings(List<string> details, string baseline, string candidate)
    {
        var baselineVersion = ReadToolVersion(baseline);
        var candidateVersion = ReadToolVersion(candidate);
        if (baselineVersion == null)
            details.Add($"warning: cannot read baseline tool version '{baseline}'");
        if (candidateVersion == null)
            details.Add($"warning: cannot read candidate tool version '{candidate}'");
        if (baselineVersion is { } first && candidateVersion is { } second &&
            (first.Major != second.Major || Math.Abs(first.Minor - second.Minor) > 1))
            details.Add($"warning: tool version age differs: baseline {baseline}, candidate {candidate}");
    }

    private static (int Major, int Minor)? ReadToolVersion(string version)
    {
        var match = ToolVersionPattern.Match(version);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
            return null;
        return (major, minor);
    }

    private static string FormatCap(string name, double value, double limit, string unit)
        => FormattableString.Invariant($"{name} {value:0.######} {unit} above limit {limit:G} {unit}");

    // The 300 MB cap covers interactive reads only. See the baseline gate in the perf README.
    private static bool IsInteractiveRead(string name)
        => name.StartsWith("impact.", StringComparison.Ordinal)
           || name.StartsWith("context.", StringComparison.Ordinal)
           || name is "search" or "grep" or "find_symbol" or "outline" or "navigate" or "get_symbol"
               or "get_annotations.document" or "grep.ignore_case";

    private static bool IsImpactDepth6(string name)
    {
        var parts = name.Split('.');
        return parts.Length >= 3 && parts[0] == "impact" && parts[^2] == "d6";
    }

    private static bool IsContext(string name) => name.StartsWith("context.", StringComparison.Ordinal);
}
