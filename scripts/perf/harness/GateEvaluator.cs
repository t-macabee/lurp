using System.Globalization;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal static class GateEvaluator
{
    // Only these operations fail the gate when the time ratio exceeds the limit.
    private static readonly string[] TimeFailTrackOperations =
    [
        "full_index",
        "incremental_1_file",
        "incremental_20_files",
        "diff",
        "dead_candidates"
    ];

    public static int Evaluate(HarnessOptions options)
    {
        var parts = options.Evaluate!.Split(',', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            throw new HarnessError("--evaluate expects two comma-separated result files: --evaluate=<4x.json>,<16x.json>.");

        var small = GateResult.Read(parts[0].Trim(), "4x");
        var large = GateResult.Read(parts[1].Trim(), "16x");
        var comparison = new ResultComparison(small, large, TimeFailTrackOperations);
        var smallFixture = ValidateFixture(small, 4, comparison.FileFailures);
        var largeFixture = ValidateFixture(large, 16, comparison.FileFailures);
        if (smallFixture != null && largeFixture != null)
        {
            var smallVersion = JsonTools.GetString(smallFixture, "generator_version");
            var largeVersion = JsonTools.GetString(largeFixture, "generator_version");
            if (string.IsNullOrEmpty(smallVersion) || smallVersion != largeVersion)
                comparison.FileFailures.Add($"fixture.generator_version is missing or differs: 4x '{smallVersion ?? "null"}', 16x '{largeVersion ?? "null"}'");

            var smallSeed = JsonTools.GetInt(smallFixture, "seed");
            var largeSeed = JsonTools.GetInt(largeFixture, "seed");
            if (smallSeed == null || smallSeed != largeSeed)
                comparison.FileFailures.Add($"fixture.seed is missing or differs: 4x '{smallSeed?.ToString(CultureInfo.InvariantCulture) ?? "null"}', 16x '{largeSeed?.ToString(CultureInfo.InvariantCulture) ?? "null"}'");
        }

        foreach (var operation in comparison.Operations)
        {
            if (operation.Failure != null)
            {
                comparison.AddRow(operation, [], []);
                continue;
            }

            var failures = new List<string>();
            var warnings = new List<string>();
            if (operation.MemoryRatio > options.MaxRatio)
                failures.Add(ResultFile.FormatRatioReason("memory", operation.MemoryRatio, options.MaxRatio));
            if (operation.TimeRatio > options.MaxRatio)
            {
                var reasons = TimeFailTrackOperations.Contains(operation.Name) ? failures : warnings;
                reasons.Add(ResultFile.FormatRatioReason("time", operation.TimeRatio, options.MaxRatio));
            }
            comparison.AddRow(operation, failures, warnings);
        }

        return comparison.WriteReport(
            "| operation | time 4x | time 16x | time ratio | memory 4x | memory 16x | memory ratio | verdict |",
            options.Summary);
    }

    private static JsonObject? ValidateFixture(GateResult result, int expectedSize, List<string> failures)
    {
        if (result.Root["solution"] != null)
            failures.Add($"{result.Label} result: solution must be null");
        if (result.Root["fixture"] is not JsonObject fixture)
        {
            failures.Add($"{result.Label} result: fixture is missing or is not an object");
            return null;
        }

        var size = JsonTools.GetInt(fixture, "size");
        if (size != expectedSize)
            failures.Add($"{result.Label} result: fixture.size is {size?.ToString(CultureInfo.InvariantCulture) ?? "missing or invalid"}, expected {expectedSize}");
        return fixture;
    }
}
