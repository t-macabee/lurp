using System.Globalization;
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
        var includeCompletenessDetail = args.Contains("--completeness-detail");
        var scopeArg = HandlerBootstrap.GetArgValue(args, "--scope=");
        var changeObjective = HandlerBootstrap.GetArgValue(args, "--change-objective=");
        var affectedProjects = GetRepeatableArgs(args, "--affected-project=");
        var constraints = GetRepeatableArgs(args, "--constraint=");
        var topologyAnnotations = GetRepeatableArgs(args, "--topology-annotation=");
        var targetTopology = ParseTargetTopology(GetRepeatableArgs(args, "--target-hop="));
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
            // Resolve the git root from the requested snapshot, not from the
            // latest complete snapshot — the capsule's verification suggestions
            // (owning test project, solution path) must describe the workspace
            // the snapshot was taken from. Falling back to LoadLatestSnapshot()
            // silently attributed the wrong workspace when --snapshot= pointed
            // at an older snapshot.
            var gitRoot = store.GetSnapshotGitRoot(snapshotId);
            var assemblyOptions = new ContextAssemblyOptions(
                intent, budget, maxHops, includeGenerated,
                scopeArg, affectedProjects, changeObjective, constraints,
                targetTopology, topologyAnnotations, gitRoot, includeCompletenessDetail);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions, store);
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
        var json = ContextCapsuleJson.Serialize(capsule);
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

    private static List<string> GetRepeatableArgs(string[] args, string prefix)
        => args.Where(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(arg => arg[prefix.Length..])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();

    private static List<ImpactPath> ParseTargetTopology(IEnumerable<string> values)
    {
        var paths = new List<ImpactPath>();
        foreach (var value in values)
        {
            var fields = value.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length is < 3 or > 4)
                throw new ArgumentException("--target-hop must be sourceSymbolId,targetSymbolId,edgeKind[,provenance].");
            paths.Add(new ImpactPath(
            [
                new ImpactHop(fields[0], fields[1], fields[2], fields.Length == 4 ? fields[3] : "caller_supplied"),
            ]));
        }
        return paths;
    }
}
