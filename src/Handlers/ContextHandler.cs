using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class ContextHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        var fileArg = HandlerBootstrap.GetArgValue(args, "--file=");
        var lineArg = HandlerBootstrap.GetArgValue(args, "--line=");
        var intentArg = HandlerBootstrap.GetArgValue(args, "--intent=") ?? "inspect";
        var budgetArg = HandlerBootstrap.GetArgValue(args, "--budget=");
        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var maxHopsArg = HandlerBootstrap.GetArgValue(args, "--max-hops=");
        var includeGenerated = args.Contains("--include-generated");
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        bool hasSymbol = !string.IsNullOrEmpty(symbolArg);
        bool hasFile = !string.IsNullOrEmpty(fileArg) && !string.IsNullOrEmpty(lineArg);
        if (!hasSymbol && !hasFile)
        {
            Console.Error.WriteLine("ERROR: Either --symbol=<symbolId> or --file=<path> --line=<line> is required for --mode=context.");
            Environment.Exit(1);
        }

        var intent = ParseIntent(intentArg);
        var budget = ParsePositiveInt(budgetArg, 8000, "--budget");
        var maxHops = ParsePositiveInt(maxHopsArg, 3, "--max-hops");
        var lineNumber = ParseLineNumber(hasFile, lineArg);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);
            var lookup = new ContextLookup(snapshotId, symbolArg, fileArg, lineNumber);
            var assemblyOptions = new ContextAssemblyOptions(intent, budget, maxHops, includeGenerated);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions);
            WriteCapsuleOutput(capsule, outputDirArg);
        }
        finally
        {
            store.Close();
        }
    }

    private static ContextIntent ParseIntent(string intentArg)
    {
        return intentArg.ToLowerInvariant() switch
        {
            "inspect" => ContextIntent.Inspect,
            "modify" => ContextIntent.Modify,
            "diagnose" => ContextIntent.Diagnose,
            _ => throw new ArgumentException("--intent must be one of: inspect, modify, diagnose.")
        };
    }

    private static int ParsePositiveInt(string? arg, int defaultValue, string flagName)
    {
        if (string.IsNullOrEmpty(arg))
            return defaultValue;

        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            Console.Error.WriteLine($"ERROR: {flagName} must be a positive integer.");
            Environment.Exit(1);
        }

        return value;
    }

    private static int? ParseLineNumber(bool hasFile, string? lineArg)
    {
        if (!hasFile)
            return null;

        if (!int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln) || ln < 1)
        {
            Console.Error.WriteLine("ERROR: --line must be a positive integer.");
            Environment.Exit(1);
        }

        return ln;
    }

    private static void WriteCapsuleOutput(ContextCapsule capsule, string outputDirArg)
    {
        var json = JsonSerializer.Serialize(capsule, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        var safeName = capsule.Anchor.SymbolId
            .Replace('|', '_')
            .Replace(':', '_')
            .Replace('/', '_')
            .Replace('\\', '_');
        var outputFileName = $"capsule-{safeName}.json";
        var outputPath = Path.Combine(Path.GetFullPath(outputDirArg), outputFileName);
        File.WriteAllText(outputPath, json);
        Console.WriteLine(json);
    }
}
