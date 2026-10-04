using System.Globalization;

namespace Lurp.PerfHarness;

internal sealed class HarnessOptions
{
    public const string Usage =
        "usage: dotnet run --project scripts/perf/harness -c Release -- " +
        "--fixture=<generated dir> | --solution=<path to .sln/.slnx> --work=<dir> --out=<result.json> " +
        "[--protocol=gate] [--runs=5] [--warmup=1] [--anchors=<json>] [--lurp-cmd=\"<exe and leading args>\"] [--keep-work]\n" +
        "       dotnet run --project scripts/perf/harness -c Release -- --evaluate=<4x.json>,<16x.json> [--max-ratio=5] [--summary=<file>]";

    public string Work { get; private set; } = "";

    public string Out { get; private set; } = "";

    public string? Fixture { get; private set; }

    public string? Solution { get; private set; }

    public string? AnchorsPath { get; private set; }

    public string? LurpCmd { get; private set; }

    public string? Evaluate { get; private set; }

    public string? Summary { get; private set; }

    public double MaxRatio { get; private set; } = 5.0;

    public int Runs { get; private set; } = 5;

    public int Warmup { get; private set; } = 1;

    public string Protocol { get; private set; } = "custom";

    public bool KeepWork { get; private set; }

    public static HarnessOptions Parse(string[] args)
    {
        var options = new HarnessOptions();
        string? runsRaw = null;
        string? warmupRaw = null;
        string? protocolRaw = null;
        string? maxRatioRaw = null;
        foreach (var arg in args)
        {
            if (TryValue(arg, "--fixture=", out var fixture))
                options.Fixture = fixture;
            else if (TryValue(arg, "--solution=", out var solution))
                options.Solution = solution;
            else if (TryValue(arg, "--work=", out var work))
                options.Work = work;
            else if (TryValue(arg, "--out=", out var outPath))
                options.Out = outPath;
            else if (TryValue(arg, "--runs=", out var runs))
                runsRaw = runs;
            else if (TryValue(arg, "--warmup=", out var warmup))
                warmupRaw = warmup;
            else if (TryValue(arg, "--protocol=", out var protocol))
                protocolRaw = protocol;
            else if (TryValue(arg, "--anchors=", out var anchors))
                options.AnchorsPath = anchors;
            else if (TryValue(arg, "--lurp-cmd=", out var lurpCmd))
                options.LurpCmd = lurpCmd;
            else if (TryValue(arg, "--evaluate=", out var evaluate))
                options.Evaluate = evaluate;
            else if (TryValue(arg, "--max-ratio=", out var maxRatio))
                maxRatioRaw = maxRatio;
            else if (TryValue(arg, "--summary=", out var summary))
                options.Summary = summary;
            else if (string.Equals(arg, "--keep-work", StringComparison.Ordinal))
                options.KeepWork = true;
            else
                throw new HarnessError($"unknown argument '{arg}'.");
        }

        if (options.Evaluate != null)
        {
            if (options.Fixture != null || options.Solution != null || !string.IsNullOrEmpty(options.Work) || !string.IsNullOrEmpty(options.Out)
                || options.AnchorsPath != null || options.LurpCmd != null || options.KeepWork
                || runsRaw != null || warmupRaw != null || protocolRaw != null)
                throw new HarnessError("--evaluate cannot be combined with run options (--fixture, --solution, --work, --out, --anchors, --lurp-cmd, --keep-work, --protocol, --runs, --warmup).");
            if (maxRatioRaw != null)
                options.MaxRatio = ParseRatio(maxRatioRaw);
            return options;
        }

        if (maxRatioRaw != null)
            throw new HarnessError("--max-ratio is only valid together with --evaluate.");
        if (options.Summary != null)
            throw new HarnessError("--summary is only valid together with --evaluate.");

        if (runsRaw != null)
            options.Runs = ParseInt("--runs", runsRaw);
        if (warmupRaw != null)
            options.Warmup = ParseInt("--warmup", warmupRaw);
        if (protocolRaw != null)
        {
            if (runsRaw != null || warmupRaw != null)
                throw new HarnessError("--runs and --warmup cannot be combined with --protocol; the protocol sets both.");
            if (!string.Equals(protocolRaw, "gate", StringComparison.OrdinalIgnoreCase))
                throw new HarnessError($"unknown --protocol '{protocolRaw}'; supported: gate.");
            options.Protocol = "gate";
            options.Runs = 5;
            options.Warmup = 3;
        }

        if (string.IsNullOrWhiteSpace(options.Fixture) == string.IsNullOrWhiteSpace(options.Solution))
            throw new HarnessError("exactly one of --fixture=<dir> or --solution=<path> is required.");
        if (string.IsNullOrWhiteSpace(options.Work))
            throw new HarnessError("--work=<dir> is required.");
        if (string.IsNullOrWhiteSpace(options.Out))
            throw new HarnessError("--out=<result.json> is required.");
        if (options.Runs < 1)
            throw new HarnessError("--runs must be at least 1.");
        if (options.Warmup < 0)
            throw new HarnessError("--warmup must be 0 or more.");
        if (options.AnchorsPath != null && options.Fixture != null)
            throw new HarnessError("--anchors is only used with --solution runs; a generated fixture carries its anchors in perf-manifest.json.");

        return options;
    }

    private static bool TryValue(string arg, string prefix, out string value)
    {
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = "";
        return false;
    }

    private static int ParseInt(string name, string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new HarnessError($"{name} must be an integer, got '{raw}'.");

        return value;
    }

    private static double ParseRatio(string raw)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new HarnessError($"--max-ratio must be a positive number, got '{raw}'.");

        return value;
    }
}
