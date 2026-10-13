using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

/// <summary>
///     Reading a harness result file and writing the markdown report. GateEvaluator and
///     BaselineEvaluator share these so the two gates read and print results the same way.
/// </summary>
internal static class ResultFile
{
    internal static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    internal static string FormatRatio(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    internal static void WriteReport(string report, string? summaryPath)
    {
        Console.Write(report);

        if (summaryPath != null)
        {
            var fullPath = Path.GetFullPath(summaryPath);
            var parent = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(fullPath, report, new UTF8Encoding(false));
            Console.WriteLine($"gate summary written: {fullPath}");
        }
    }

    internal static JsonObject ReadResult(string path)
    {
        if (!File.Exists(path))
            throw new HarnessError($"result file '{path}' does not exist.");
        return JsonTools.TryParseObject(File.ReadAllText(path))
               ?? throw new HarnessError($"'{path}' is not readable JSON.");
    }

    internal static Dictionary<string, JsonObject> ReadOperations(string path, JsonObject root)
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

    internal static int? ReadInt(JsonObject root, string key)
        => root[key] is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    internal static bool ReadFailed(JsonObject op)
        => op["failed"] is JsonValue value && value.TryGetValue<bool>(out var failed) && failed;

    internal static bool ReadSkipped(JsonObject op, out string reason)
    {
        reason = "";
        if (op["skipped"] is JsonValue value && value.TryGetValue<bool>(out var skipped) && skipped)
        {
            reason = JsonTools.GetString(op, "skip_reason") ?? "";
            return true;
        }

        return false;
    }

    internal static bool TryReadMedian(JsonObject op, out double wallMs, out double peakMb)
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
