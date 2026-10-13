using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

/// <summary>
///     The baseline perf gate: compares a candidate result file against a committed baseline
///     result file and fails on a memory regression or an over-cap interactive read.
/// </summary>
internal static class BaselineEvaluator
{
    // Candidate peak above this factor of the baseline peak fails, for every operation.
    private const double MemoryFactor = 1.25;

    // Candidate wall above this factor of the baseline wall warns, for every operation.
    private const double TimeFactor = 1.25;

    // Peak working set above this fails for an interactive read (impact, context, search, grep, find_symbol only).
    private const double InteractiveReadPeakCapMb = 300;

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
        "diff",
        "dead_candidates"
    ];

    // --solution runs skip edit and touch operations for these reasons; they carry no measurement.
    private static readonly string[] AllowedSkipReasons =
    [
        SkipReasons.NeverEdits,
        SkipReasons.NeverTouches
    ];

    public static int Evaluate(HarnessOptions options)
    {
        var baselinePath = options.Baseline!;
        var candidatePath = options.Candidate!;

        var rootBaseline = ResultFile.ReadResult(baselinePath);
        var rootCandidate = ResultFile.ReadResult(candidatePath);

        var fileFailures = new List<string>();
        var gateFailures = 0;
        var warnings = 0;

        var protocolBaseline = JsonTools.GetString(rootBaseline, "protocol");
        var protocolCandidate = JsonTools.GetString(rootCandidate, "protocol");
        if (protocolBaseline != "gate")
            fileFailures.Add($"baseline: protocol is '{protocolBaseline ?? "null"}', expected 'gate'");
        if (protocolCandidate != "gate")
            fileFailures.Add($"candidate: protocol is '{protocolCandidate ?? "null"}', expected 'gate'");

        var runsBaseline = ResultFile.ReadInt(rootBaseline, "runs");
        var runsCandidate = ResultFile.ReadInt(rootCandidate, "runs");
        if (runsBaseline == null || runsCandidate == null)
            fileFailures.Add("runs is missing in one of the result files");
        else if (runsBaseline != runsCandidate)
            fileFailures.Add($"runs differ: baseline has {runsBaseline}, candidate has {runsCandidate}");

        var warmupBaseline = ResultFile.ReadInt(rootBaseline, "warmup");
        var warmupCandidate = ResultFile.ReadInt(rootCandidate, "warmup");
        if (warmupBaseline == null || warmupCandidate == null)
            fileFailures.Add("warmup is missing in one of the result files");
        else if (warmupBaseline != warmupCandidate)
            fileFailures.Add($"warmup differs: baseline has {warmupBaseline}, candidate has {warmupCandidate}");

        var commitBaseline = JsonTools.GetString(rootBaseline, "solution_commit");
        var commitCandidate = JsonTools.GetString(rootCandidate, "solution_commit");
        if (string.IsNullOrEmpty(commitBaseline) || string.IsNullOrEmpty(commitCandidate))
            fileFailures.Add($"solution_commit is missing: baseline '{commitBaseline ?? "null"}', candidate '{commitCandidate ?? "null"}'");
        else if (!string.Equals(commitBaseline, commitCandidate, StringComparison.Ordinal))
            fileFailures.Add($"solution_commit differs: baseline '{commitBaseline}', candidate '{commitCandidate}'");

        var runnerImageBaseline = JsonTools.GetString(rootBaseline, "runner_image");
        var runnerImageCandidate = JsonTools.GetString(rootCandidate, "runner_image");
        var runnerImageDiffers = !string.Equals(runnerImageBaseline, runnerImageCandidate, StringComparison.Ordinal);
        if (runnerImageDiffers)
            warnings++;

        var opsBaseline = ResultFile.ReadOperations(baselinePath, rootBaseline);
        var opsCandidate = ResultFile.ReadOperations(candidatePath, rootCandidate);

        var rows = new List<string>();
        var excluded = new List<string>();
        var hasMeasuredImpact = false;
        var hasMeasuredContext = false;

        foreach (var name in opsBaseline.Keys.Union(opsCandidate.Keys).Union(RequiredOperations).OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!opsBaseline.TryGetValue(name, out var opBaseline))
            {
                rows.Add(opsCandidate.ContainsKey(name)
                    ? $"| {name} | - | - | - | - | - | - | FAIL (missing in baseline result) |"
                    : $"| {name} | - | - | - | - | - | - | FAIL (missing in both results) |");
                gateFailures++;
                continue;
            }

            if (!opsCandidate.TryGetValue(name, out var opCandidate))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (missing in candidate result) |");
                gateFailures++;
                continue;
            }

            if (ResultFile.ReadFailed(opBaseline))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (failed in baseline result) |");
                gateFailures++;
                continue;
            }

            if (ResultFile.ReadFailed(opCandidate))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (failed in candidate result) |");
                gateFailures++;
                continue;
            }

            var skipBaseline = ResultFile.ReadSkipped(opBaseline, out var reasonBaseline);
            var skipCandidate = ResultFile.ReadSkipped(opCandidate, out var reasonCandidate);
            if (skipBaseline && skipCandidate)
            {
                if (!IsAllowedSkip(reasonBaseline))
                {
                    rows.Add($"| {name} | - | - | - | - | - | - | FAIL (skipped in baseline result: {reasonBaseline}) |");
                    gateFailures++;
                    continue;
                }

                if (!IsAllowedSkip(reasonCandidate))
                {
                    rows.Add($"| {name} | - | - | - | - | - | - | FAIL (skipped in candidate result: {reasonCandidate}) |");
                    gateFailures++;
                    continue;
                }

                excluded.Add(name);
                continue;
            }

            if (skipBaseline || skipCandidate)
            {
                var which = skipBaseline ? "baseline" : "candidate";
                var reason = skipBaseline ? reasonBaseline : reasonCandidate;
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (skipped in {which} result only: {reason}) |");
                gateFailures++;
                continue;
            }

            if (name.StartsWith("impact.", StringComparison.Ordinal))
                hasMeasuredImpact = true;
            if (name.StartsWith("context.", StringComparison.Ordinal))
                hasMeasuredContext = true;

            if (!ResultFile.TryReadMedian(opBaseline, out var wallBaseline, out var peakBaseline))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (median missing or null in baseline result) |");
                gateFailures++;
                continue;
            }

            if (!ResultFile.TryReadMedian(opCandidate, out var wallCandidate, out var peakCandidate))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (median missing or null in candidate result) |");
                gateFailures++;
                continue;
            }

            if (wallBaseline <= 0 || peakBaseline <= 0)
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (median is 0 in baseline result) |");
                gateFailures++;
                continue;
            }

            if (wallCandidate <= 0 || peakCandidate <= 0)
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (median is 0 in candidate result) |");
                gateFailures++;
                continue;
            }

            var timeRatio = wallCandidate / wallBaseline;
            var memoryRatio = peakCandidate / peakBaseline;
            var failReasons = new List<string>();
            var warnReasons = new List<string>();
            if (memoryRatio > MemoryFactor)
                failReasons.Add($"memory ratio {ResultFile.FormatRatio(memoryRatio)} > {ResultFile.FormatRatio(MemoryFactor)}");
            if (IsInteractiveRead(name) && peakCandidate > InteractiveReadPeakCapMb)
                failReasons.Add($"interactive read peak {ResultFile.Format(peakCandidate)} MB > {ResultFile.Format(InteractiveReadPeakCapMb)} MB");
            if (timeRatio > TimeFactor)
                warnReasons.Add($"time ratio {ResultFile.FormatRatio(timeRatio)} > {ResultFile.FormatRatio(TimeFactor)}");
            if (IsImpactDepth6(name) && wallCandidate > ImpactDepth6CapMs)
                warnReasons.Add($"impact depth 6 {ResultFile.Format(wallCandidate)} ms > {ResultFile.Format(ImpactDepth6CapMs)} ms");
            if (IsContext(name) && wallCandidate > ContextCapMs)
                warnReasons.Add($"context {ResultFile.Format(wallCandidate)} ms > {ResultFile.Format(ContextCapMs)} ms");
            if (IsSearch(name) && wallCandidate > SearchCapMs)
                warnReasons.Add($"search {ResultFile.Format(wallCandidate)} ms > {ResultFile.Format(SearchCapMs)} ms");

            if (warnReasons.Count > 0)
                warnings++;
            if (failReasons.Count > 0)
                gateFailures++;
            string verdict;
            if (failReasons.Count > 0)
                verdict = $"FAIL ({string.Join("; ", failReasons.Concat(warnReasons))})";
            else if (warnReasons.Count > 0)
                verdict = $"WARN ({string.Join("; ", warnReasons)})";
            else
                verdict = "PASS";
            rows.Add($"| {name} | {ResultFile.Format(wallBaseline)} | {ResultFile.Format(wallCandidate)} | {ResultFile.FormatRatio(timeRatio)} | {ResultFile.Format(peakBaseline)} | {ResultFile.Format(peakCandidate)} | {ResultFile.FormatRatio(memoryRatio)} | {verdict} |");
        }

        if (!hasMeasuredImpact)
            fileFailures.Add("no measured impact.* operation");
        if (!hasMeasuredContext)
            fileFailures.Add("no measured context.* operation");

        var report = new StringBuilder();
        foreach (var failure in fileFailures)
            report.AppendLine(CultureInfo.InvariantCulture, $"gate failure: {failure}");
        if (fileFailures.Count > 0)
            report.AppendLine();
        if (runnerImageDiffers)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"warning: runner image differs: baseline {runnerImageBaseline ?? "null"}, candidate {runnerImageCandidate ?? "null"}");
            report.AppendLine();
        }

        report.AppendLine("| operation | time baseline | time candidate | time ratio | memory baseline | memory candidate | memory ratio | verdict |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var row in rows)
            report.AppendLine(row);
        if (excluded.Count > 0)
        {
            report.AppendLine();
            report.AppendLine(CultureInfo.InvariantCulture, $"skipped as solution-only (not compared): {string.Join(", ", excluded)}");
        }

        report.AppendLine();
        var verdictLine = gateFailures + fileFailures.Count > 0 ? "FAIL" : "PASS";
        report.AppendLine(CultureInfo.InvariantCulture, $"gate verdict: {verdictLine} ({gateFailures + fileFailures.Count} failure(s), {warnings} warning(s))");

        ResultFile.WriteReport(report.ToString(), options.Summary);

        return gateFailures + fileFailures.Count > 0 ? 1 : 0;
    }

    private static bool IsAllowedSkip(string reason) => AllowedSkipReasons.Contains(reason);

    // Decision B87: the 300 MB cap covers interactive reads only.
    private static bool IsInteractiveRead(string name)
        => name.StartsWith("impact.", StringComparison.Ordinal)
           || name.StartsWith("context.", StringComparison.Ordinal)
           || name is "search" or "grep" or "find_symbol";

    private static bool IsImpactDepth6(string name)
    {
        var parts = name.Split('.');
        return parts.Length >= 3 && parts[0] == "impact" && parts[^2] == "d6";
    }

    private static bool IsContext(string name) => name.StartsWith("context.", StringComparison.Ordinal);

    private static bool IsSearch(string name) => string.Equals(name, "search", StringComparison.Ordinal);
}
