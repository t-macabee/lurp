using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal static class GateEvaluator
{
    // A time-ratio breach fails the gate for these operations only; every other operation only warns on time.
    private static readonly string[] TimeFailTrackOperations =
    [
        "full_index",
        "incremental_1_file",
        "incremental_20_files",
        "diff",
        "dead_candidates"
    ];

    // Skip reasons only --solution runs produce (see Program.cs); they carry no measurement and do not fail the gate.
    private static readonly string[] SolutionOnlySkipReasons =
    [
        "--solution runs never edit files",
        "--solution runs never touch files",
        "no anchors available",
        "no anchors available (pass --anchors for --solution runs)"
    ];

    public static int Evaluate(HarnessOptions options)
    {
        var parts = options.Evaluate!.Split(',', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new HarnessError("--evaluate expects two comma-separated result files: --evaluate=<4x.json>,<16x.json>.");

        var path4 = parts[0].Trim();
        var path16 = parts[1].Trim();
        var maxRatio = options.MaxRatio;

        var root4 = ReadResult(path4);
        var root16 = ReadResult(path16);

        var fileFailures = new List<string>();
        var gateFailures = 0;
        var warnings = 0;

        var protocol4 = JsonTools.GetString(root4, "protocol");
        var protocol16 = JsonTools.GetString(root16, "protocol");
        if (protocol4 != "gate")
            fileFailures.Add($"{path4}: protocol is '{protocol4 ?? "null"}', expected 'gate'");
        if (protocol16 != "gate")
            fileFailures.Add($"{path16}: protocol is '{protocol16 ?? "null"}', expected 'gate'");

        var runs4 = ReadInt(root4, "runs");
        var runs16 = ReadInt(root16, "runs");
        if (runs4 == null || runs16 == null)
            fileFailures.Add("runs is missing in one of the result files");
        else if (runs4 != runs16)
            fileFailures.Add($"runs differ between the two files: 4x has {runs4}, 16x has {runs16}");

        var warmup4 = ReadInt(root4, "warmup");
        var warmup16 = ReadInt(root16, "warmup");
        if (warmup4 == null || warmup16 == null)
            fileFailures.Add("warmup is missing in one of the result files");
        else if (warmup4 != warmup16)
            fileFailures.Add($"warmup differs between the two files: 4x has {warmup4}, 16x has {warmup16}");

        var ops4 = ReadOperations(path4, root4);
        var ops16 = ReadOperations(path16, root16);

        var rows = new List<string>();
        var excluded = new List<string>();

        foreach (var name in ops4.Keys.Union(ops16.Keys).Union(TimeFailTrackOperations).OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (!ops4.TryGetValue(name, out var op4))
            {
                rows.Add(ops16.ContainsKey(name)
                    ? $"| {name} | - | - | - | - | - | - | FAIL (missing in 4x result) |"
                    : $"| {name} | - | - | - | - | - | - | FAIL (missing in both results) |");
                gateFailures++;
                continue;
            }

            if (!ops16.TryGetValue(name, out var op16))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (missing in 16x result) |");
                gateFailures++;
                continue;
            }

            if (ReadFailed(op4))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (failed in 4x result) |");
                gateFailures++;
                continue;
            }

            if (ReadFailed(op16))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (failed in 16x result) |");
                gateFailures++;
                continue;
            }

            var skip4 = ReadSkipped(op4, out var reason4);
            var skip16 = ReadSkipped(op16, out var reason16);
            if (skip4 && !IsSolutionOnlySkip(reason4))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (skipped in 4x result: {reason4}) |");
                gateFailures++;
                continue;
            }

            if (skip16 && !IsSolutionOnlySkip(reason16))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (skipped in 16x result: {reason16}) |");
                gateFailures++;
                continue;
            }

            if (skip4 || skip16)
            {
                excluded.Add(name);
                continue;
            }

            if (!TryReadMedian(op4, out var wall4, out var peak4))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (median missing or null in 4x result) |");
                gateFailures++;
                continue;
            }

            if (!TryReadMedian(op16, out var wall16, out var peak16))
            {
                rows.Add($"| {name} | - | - | - | - | - | - | FAIL (median missing or null in 16x result) |");
                gateFailures++;
                continue;
            }

            if (wall4 <= 0 || peak4 <= 0)
            {
                rows.Add($"| {name} | {Format(wall4)} | - | - | {Format(peak4)} | - | - | FAIL (median is 0 in 4x result) |");
                gateFailures++;
                continue;
            }

            if (wall16 <= 0 || peak16 <= 0)
            {
                rows.Add($"| {name} | - | {Format(wall16)} | - | - | {Format(peak16)} | - | FAIL (median is 0 in 16x result) |");
                gateFailures++;
                continue;
            }

            var timeRatio = wall16 / wall4;
            var memoryRatio = peak16 / peak4;
            var timeFails = TimeFailTrackOperations.Contains(name);
            var reasons = new List<string>();
            var warn = false;
            if (memoryRatio > maxRatio)
                reasons.Add($"memory ratio {FormatRatio(memoryRatio)} > {FormatRatio(maxRatio)}");
            if (timeRatio > maxRatio)
            {
                if (timeFails)
                    reasons.Add($"time ratio {FormatRatio(timeRatio)} > {FormatRatio(maxRatio)}");
                else
                    warn = true;
            }

            if (warn)
                warnings++;
            if (reasons.Count > 0)
                gateFailures++;
            var verdict = reasons.Count > 0 ? $"FAIL ({string.Join("; ", reasons)})" : warn ? "WARN" : "PASS";
            rows.Add($"| {name} | {Format(wall4)} | {Format(wall16)} | {FormatRatio(timeRatio)} | {Format(peak4)} | {Format(peak16)} | {FormatRatio(memoryRatio)} | {verdict} |");
        }

        var report = new StringBuilder();
        foreach (var failure in fileFailures)
            report.AppendLine(CultureInfo.InvariantCulture, $"gate failure: {failure}");
        if (fileFailures.Count > 0)
            report.AppendLine();
        report.AppendLine("| operation | time 4x | time 16x | time ratio | memory 4x | memory 16x | memory ratio | verdict |");
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

        Console.Write(report.ToString());

        if (options.Summary != null)
        {
            var summaryPath = Path.GetFullPath(options.Summary);
            var parent = Path.GetDirectoryName(summaryPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(summaryPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"gate summary written: {summaryPath}");
        }

        return gateFailures + fileFailures.Count > 0 ? 1 : 0;
    }

    private static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    private static string FormatRatio(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    private static bool IsSolutionOnlySkip(string reason) => SolutionOnlySkipReasons.Contains(reason);

    private static JsonObject ReadResult(string path)
    {
        if (!File.Exists(path))
            throw new HarnessError($"result file '{path}' does not exist.");
        return JsonTools.TryParseObject(File.ReadAllText(path))
               ?? throw new HarnessError($"'{path}' is not readable JSON.");
    }

    private static Dictionary<string, JsonObject> ReadOperations(string path, JsonObject root)
    {
        if (root["operations"] is not JsonArray array)
            throw new HarnessError($"'{path}' has no operations array.");
        var operations = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in array)
        {
            if (node is not JsonObject op)
                throw new HarnessError($"'{path}' has a non-object entry in operations.");
            var name = JsonTools.GetString(op, "name");
            if (string.IsNullOrEmpty(name))
                throw new HarnessError($"'{path}' has an operation entry without a name.");
            if (operations.ContainsKey(name))
                throw new HarnessError($"'{path}' has duplicate operation '{name}'.");
            operations[name] = op;
        }

        return operations;
    }

    private static int? ReadInt(JsonObject root, string key)
        => root[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static bool ReadFailed(JsonObject op)
        => op["failed"] is JsonValue value && value.TryGetValue<bool>(out var failed) && failed;

    private static bool ReadSkipped(JsonObject op, out string reason)
    {
        reason = "";
        if (op["skipped"] is JsonValue value && value.TryGetValue<bool>(out var skipped) && skipped)
        {
            reason = JsonTools.GetString(op, "skip_reason") ?? "";
            return true;
        }

        return false;
    }

    private static bool TryReadMedian(JsonObject op, out double wallMs, out double peakMb)
    {
        wallMs = 0;
        peakMb = 0;
        if (op["median"] is not JsonObject median)
            return false;
        if (median["wall_ms"] is not JsonValue wall || !wall.TryGetValue<double>(out wallMs))
            return false;
        if (median["peak_working_set_mb"] is not JsonValue peak || !peak.TryGetValue<double>(out peakMb))
            return false;
        return true;
    }
}
