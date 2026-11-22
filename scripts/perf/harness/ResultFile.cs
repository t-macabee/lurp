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
    internal const double BytesPerMiB = 1024 * 1024;

    internal static string Format(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    internal static string FormatRatio(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    ///     A peak working set measurement. Exact values come from a result that records
    ///     <c>peak_working_set_bytes</c>. A legacy result records only whole MiB; its
    ///     exact byte value is unknown inside (mb - 1, mb] MiB.
    /// </summary>
    internal readonly record struct PeakMeasurement(double Bytes, bool Exact)
    {
        public double MinBytes => Exact ? Bytes : Math.Max(0, Bytes - BytesPerMiB);

        public double MaxBytes => Bytes;

        public static PeakMeasurement ExactBytes(double bytes) => new(bytes, true);

        public static PeakMeasurement LegacyMb(double megabytes) => new(megabytes * BytesPerMiB, false);
    }

    internal static long CeilingMiB(double bytes) => (long)Math.Ceiling(bytes / BytesPerMiB);

    internal static string FormatPeak(PeakMeasurement peak)
        => peak.Exact
            ? (peak.Bytes / BytesPerMiB).ToString("F1", CultureInfo.InvariantCulture)
            : "~" + (peak.Bytes / BytesPerMiB).ToString("F0", CultureInfo.InvariantCulture);

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

    internal static bool TryReadMedian(JsonObject op, int schemaVersion, out double wallMs, out PeakMeasurement peak)
        => TryReadMeasurements(op["median"] as JsonObject, schemaVersion, out wallMs, out peak);

    internal static bool TryReadMeasurements(JsonObject? obj, int schemaVersion, out double wallMs, out PeakMeasurement peak)
    {
        wallMs = 0;
        peak = default;
        if (obj?["wall_ms"] is not JsonValue wall || !wall.TryGetValue(out wallMs) || !double.IsFinite(wallMs))
            return false;

        if (schemaVersion >= 2)
        {
            if (obj["peak_working_set_bytes"] is not JsonValue bytesValue
                || !bytesValue.TryGetValue<double>(out var bytes)
                || !double.IsFinite(bytes))
                return false;

            peak = PeakMeasurement.ExactBytes(bytes);
            if (obj["peak_working_set_mb"] is JsonValue mbValue
                && (!mbValue.TryGetValue<double>(out var mb) || mb != CeilingMiB(bytes)))
                return false;
            return true;
        }

        // Schema 1 recorded whole MiB only. Reject a mixed record that also carries bytes.
        if (obj["peak_working_set_bytes"] != null)
            return false;
        if (obj["peak_working_set_mb"] is not JsonValue mbOnlyValue
            || !mbOnlyValue.TryGetValue<double>(out var mbOnly)
            || !double.IsFinite(mbOnly))
            return false;

        peak = PeakMeasurement.LegacyMb(mbOnly);
        return true;
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : sorted[middle - 1] / 2 + sorted[middle] / 2;
    }

    internal static string FormatRatioReason(string metric, double ratio, double limit)
        => FormattableString.Invariant($"{metric} ratio {ratio:F4} above limit {limit:G}");

    internal static string FormatPrecisionReason(string metric, double min, double max, double limit)
        => FormattableString.Invariant(
            $"{metric} ratio range {min:F4} to {max:F4} cannot be proven at or below limit {limit:G}; a schema 1 result stores whole MiB only, so record peak_working_set_bytes and refresh the baseline");
}
