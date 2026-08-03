using System.Globalization;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class ContextHandler
{
    private const string CursorKind = "capsule-tier";
    private const int DefaultTierLimit = 25;

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
        var tierArg = HandlerBootstrap.GetArgValue(args, "--tier=");
        // A capsule is one document, not a sequence, so jsonl is rejected rather than
        // faked. A single-tier continuation *is* a sequence, so it may stream.
        var outputMode = HandlerBootstrap.ParseOutputMode(args, allowJsonl: !string.IsNullOrEmpty(tierArg));
        var quiet = HandlerBootstrap.IsQuiet(args);
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

            if (!string.IsNullOrEmpty(tierArg))
            {
                RunTierContinuation(store, args, snapshotId, tierArg, symbolArg, fileArg, lineNumber,
                    maxHops, includeGenerated, outputMode);
                return;
            }

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
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions, store, store);

            var freshness = HandlerBootstrap.ComputeFreshnessStamp(store, snapshotId, args);
            HandlerBootstrap.EnforceRequireFresh(args, freshness);
            HandlerBootstrap.PrintFreshnessLine(args, freshness);

            WriteCapsuleOutput(capsule, outputDirArg, outputMode, quiet);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Serves <c>--tier=&lt;name&gt;</c> (optionally with <c>--cursor=</c>): one capsule tier,
    /// rebuilt outside the capsule budget and paged. This is the action a capsule's
    /// <c>omittedTiers: budget_exhausted</c> entry previously admitted to but offered no
    /// way to take.
    /// </summary>
    private static void RunTierContinuation(
        SqliteIndexStore store, string[] args, string snapshotId, string tierArg,
        string? symbolArg, string? fileArg, int? lineNumber, int maxHops, bool includeGenerated,
        OutputMode outputMode)
    {
        if (!ContextAssembler.TierNames.Contains(tierArg, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"ERROR: unknown --tier '{tierArg}'. Valid tiers: {string.Join(", ", ContextAssembler.TierNames)}.");
            Environment.Exit(1);
        }

        var resolvedSymbol = !string.IsNullOrEmpty(symbolArg)
            ? symbolArg
            : store.ResolveSymbolByLocation(fileArg!, lineNumber!.Value, snapshotId, includeGenerated);
        if (string.IsNullOrEmpty(resolvedSymbol))
        {
            Console.Error.WriteLine($"ERROR: no symbol found at {fileArg}:{lineNumber}; --tier needs an anchor symbol.");
            Environment.Exit(1);
            return;
        }

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--tier-limit=", DefaultTierLimit);
        var fingerprint = SequenceCursor.ComputeFingerprint(
            resolvedSymbol, tierArg,
            maxHops.ToString(CultureInfo.InvariantCulture),
            includeGenerated.ToString());
        var cursor = HandlerBootstrap.ResolveSequenceCursor(args, snapshotId, fingerprint, CursorKind);
        var offset = cursor?.Offset ?? 0;

        var page = ContextAssembler.BuildTierPage(
            store, store, snapshotId, SymbolId.Parse(resolvedSymbol), tierArg,
            maxHops, includeGenerated, offset, limit);

        var freshness = HandlerBootstrap.ComputeFreshnessStamp(store, snapshotId, args);
        HandlerBootstrap.EnforceRequireFresh(args, freshness);
        HandlerBootstrap.PrintFreshnessLine(args, freshness);

        var nextCursor = page.HasMore
            ? new SequenceCursor(snapshotId, fingerprint, CursorKind, offset + page.Items.Count).Encode()
            : null;

        if (outputMode == OutputMode.Summary)
        {
            Console.WriteLine($"tier {page.TierName} of {page.FullyQualifiedName} ({page.Kind})");
            Console.WriteLine($"  items: {page.TotalItems} total, {page.Items.Count} in this page (offset {page.Offset})");
            foreach (var item in page.Items)
                Console.WriteLine($"  {item.FullyQualifiedName}  [{item.EdgeKind}/{item.Provenance}]  {item.DocumentPath}:{item.StartLine}");
            if (nextCursor != null)
                Console.WriteLine("  more available: re-run with --cursor=<nextCursor from --output=json>.");
            return;
        }

        var meta = new
        {
            snapshot_id = snapshotId,
            freshness = HandlerBootstrap.FreshnessJson(freshness),
            symbol_id = page.SymbolId,
            fully_qualified_name = page.FullyQualifiedName,
            kind = page.Kind,
            tier = page.TierName,
            total_items = page.TotalItems,
            offset = page.Offset,
            next_cursor = nextCursor,
            // The tier is rebuilt in isolation, so no capsule token budget applies here.
            // Saying so keeps this page from being mistaken for a budgeted capsule section.
            budget_applied = false,
        };

        if (outputMode == OutputMode.Jsonl)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
            foreach (var item in page.Items)
                Console.WriteLine(JsonSerializer.Serialize(new { type = "item", item }, ContextCapsuleJson.CompactOptions));
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            meta.snapshot_id,
            meta.freshness,
            meta.symbol_id,
            meta.fully_qualified_name,
            meta.kind,
            meta.tier,
            meta.total_items,
            meta.offset,
            meta.next_cursor,
            meta.budget_applied,
            items = page.Items,
        }, ContextCapsuleJson.Options));
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

    private static void WriteCapsuleOutput(ContextCapsule capsule, string outputDirArg, OutputMode outputMode, bool quiet)
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

        // The capsule file is always written; only the stdout echo is optional. --quiet
        // exists because the default duplicates an 81 KB artifact into the caller's
        // context for no gain when the file path is all they needed.
        if (quiet)
        {
            Console.WriteLine(outputPath);
            return;
        }

        if (outputMode == OutputMode.Summary)
        {
            WriteCapsuleSummary(capsule, outputPath);
            return;
        }

        Console.WriteLine(json);
    }

    private static void WriteCapsuleSummary(ContextCapsule capsule, string outputPath)
    {
        Console.WriteLine($"capsule {capsule.Anchor.FullyQualifiedName} ({capsule.Anchor.Kind})");
        Console.WriteLine($"  snapshot: {capsule.Anchor.SnapshotId}  intent: {capsule.Anchor.Intent}  maxHops: {capsule.Anchor.MaxHops}");
        // Both numbers, because they answer different questions: the first is what
        // the budget bounded (content), the second is what loading the file costs.
        Console.WriteLine($"  content tokens: {capsule.EstimatedTokens}/{capsule.Budget}  artifact tokens: ~{capsule.EstimatedArtifactTokens}  truncated: {capsule.Truncated}");

        foreach (var (name, count) in new (string, int)[]
                 {
                     ("contracts", capsule.Contracts.Count),
                     ("directCallees", capsule.DirectCallees.Count),
                     ("directCallers", capsule.DirectCallers.Count),
                     ("registeredImplementations", capsule.RegisteredImplementations.Count),
                     ("relevantTests", capsule.RelevantTests.Count),
                     ("secondDegreeContext", capsule.SecondDegreeContext.Count),
                     ("surroundingSource", capsule.SurroundingSource.Count),
                 })
        {
            Console.WriteLine($"  {name,-28} {count}");
        }

        Console.WriteLine($"  incomingPaths: {capsule.IncomingPaths.Count}  outgoingPaths: {capsule.OutgoingPaths.Count}  uncertainties: {capsule.Uncertainties.Count}");

        foreach (var omitted in capsule.OmittedTiers)
        {
            // Only budget-bounded tiers have anything to recover. Offering the
            // continuation for an "empty" or "unresolved" tier would suggest the
            // content is behind a budget when it is either proved absent or
            // unobservable — refetching returns the same nothing.
            var recoverable = omitted.Reason is "budget_exhausted" or "summarized"
                && ContextAssembler.TierNames.Contains(omitted.Category, StringComparer.Ordinal);
            var continuation = recoverable ? $" — fetch with --tier={omitted.Category}" : string.Empty;
            Console.WriteLine($"  omitted: {omitted.Category} ({omitted.Reason}){continuation}");
        }

        Console.WriteLine($"  written: {outputPath}");
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
