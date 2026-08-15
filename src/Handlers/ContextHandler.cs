using Lurp.Workspace;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class ContextHandler
{
    private const string CursorKind = "capsule-tier";
    private const int DefaultTierLimit = 25;
    // Keep ordinary capsule paths below the legacy Windows MAX_PATH boundary even
    // when the output directory itself is moderately nested.
    private const int MaxCapsuleFileNameLength = 128;

    internal const int DefaultBudget = 8000;
    // A type anchor's callee/caller tiers scale with member fan-out, so a capsule
    // for a type routinely needs more room than a method anchor's. This default
    // only applies when the caller omits --content-budget=; an explicit budget is always
    // honored as-is.
    internal const int DefaultTypeAnchorBudget = 16000;

    internal static int DefaultBudgetFor(string? symbolArg) =>
        symbolArg is not null && SymbolId.TryParse(symbolArg, out var id) && id.IsType
            ? DefaultTypeAnchorBudget
            : DefaultBudget;

    internal static int DefaultBudgetFor(SymbolId symbolId) =>
        symbolId.IsType ? DefaultTypeAnchorBudget : DefaultBudget;

    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        var fileArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--file="));
        var lineArg = HandlerBootstrap.GetArgValue(args, "--line=");
        var intentArg = HandlerBootstrap.GetArgValue(args, "--intent=") ?? "inspect";
        var budgetArg = HandlerBootstrap.GetArgValue(args, "--content-budget=");
        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var includeGenerated = args.Contains("--include-generated");
        var includeCompletenessDetail = args.Contains("--completeness-detail");
        var scopeArg = HandlerBootstrap.GetArgValue(args, "--scope=");
        var affectedProjects = GetRepeatableArgs(args, "--affected-project=");
        var tierArg = HandlerBootstrap.GetArgValue(args, "--tier=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args, allowJsonl: !string.IsNullOrEmpty(tierArg));
        var quiet = HandlerBootstrap.IsQuiet(args);
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        bool hasSymbol = !string.IsNullOrEmpty(symbolArg);
        bool hasFile = !string.IsNullOrEmpty(fileArg) && !string.IsNullOrEmpty(lineArg);
        if (!hasSymbol && !hasFile)
        {
            HandlerBootstrap.Fail("ERROR: Either --symbol=<symbolId> or --file=<path> --line=<line> is required for --mode=context.");
        }

        var intent = ParseIntent(intentArg);
        var budget = string.IsNullOrEmpty(budgetArg)
            ? DefaultBudgetFor(symbolArg)
            : HandlerBootstrap.ParsePositiveIntArg(args, "--content-budget=", DefaultBudget);
        var maxHops = HandlerBootstrap.ParsePositiveIntArg(args, "--max-hops=", 3);
        var lineNumber = ParseLineNumber(hasFile, lineArg);

        HandlerBootstrap.WithStore<object?>(args, snapshotArg, (store, snapshotId) =>
        {
            if (hasSymbol)
            {
                symbolArg = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId, includeGenerated);
            }

            if (!string.IsNullOrEmpty(tierArg))
            {
                RunTierContinuation(store, args, snapshotId, tierArg, symbolArg, fileArg, lineNumber,
                    maxHops, includeGenerated, outputMode);
                return null;
            }

            var lookup = new ContextLookup(snapshotId, symbolArg, fileArg, lineNumber);
            var gitRoot = store.GetSnapshotGitRoot(snapshotId);
            var assemblyOptions = new ContextAssemblyOptions(
                intent, budget, maxHops, includeGenerated,
                scopeArg, affectedProjects, gitRoot, includeCompletenessDetail);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions, store, store);

            HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            WriteCapsuleOutput(capsule, outputDirArg, outputMode, quiet);
            return null;
        });
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
            HandlerBootstrap.Fail($"ERROR: unknown --tier '{tierArg}'. Valid tiers: {string.Join(", ", ContextAssembler.TierNames)}.");
        }

        var resolvedSymbol = !string.IsNullOrEmpty(symbolArg)
            ? symbolArg
            : store.ResolveSymbolByLocation(fileArg!, lineNumber!.Value, snapshotId, includeGenerated);
        if (string.IsNullOrEmpty(resolvedSymbol))
        {
            HandlerBootstrap.Fail($"ERROR: no symbol found at {fileArg}:{lineNumber}; --tier needs an anchor symbol.");
            return;
        }

        // Belt-and-suspenders: Run() already calls ValidateSymbolIdFormat(symbolArg) before
        // ever dispatching here whenever --symbol is supplied (audited: this method is
        // unreachable with an unvalidated user-supplied symbolArg today), and the file/line
        // branch above always resolves to a well-formed persisted value. Re-validating here
        // means this call stays safe even if a future caller stops going through Run()'s gate.
        ValidateSymbolIdFormat(resolvedSymbol);

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

        var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

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

    // SymbolId.Parse throws an unhandled FormatException on anything without the
    // 'docCommentId|assemblyIdentity' pipe separator it requires. The easy mistake
    // this catches: passing the fully-qualified name --mode=search prints (e.g.
    // "T:Some.Type") instead of the resolvable symbolId --mode=find-symbol returns.
    // Fail cleanly here instead of surfacing a raw stack trace out of ContextAssembler.
    private static void ValidateSymbolIdFormat(string symbolArg)
    {
        if (!symbolArg.Contains('|'))
        {
            HandlerBootstrap.Fail(
                $"ERROR: --symbol value '{symbolArg}' is not a resolvable symbolId " +
                "(expected 'docCommentId|assemblyIdentity'). Run --mode=find-symbol " +
                "--symbol=<name> first and pass its exact symbolId, not a bare FQN or search result.");
        }
    }

    private static int? ParseLineNumber(bool hasFile, string? lineArg)
    {
        if (!hasFile)
            return null;

        if (!int.TryParse(lineArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ln) || ln < 1)
        {
            HandlerBootstrap.Fail("ERROR: --line must be a positive integer.");
        }

        return ln;
    }

    internal static void WriteCapsuleOutput(ContextCapsule capsule, string outputDirArg, OutputMode outputMode, bool quiet)
    {
        var json = ContextCapsuleJson.Serialize(capsule);
        var outputPath = GetCapsuleOutputPath(outputDirArg, capsule.Anchor.SymbolId);
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

    internal static string GetCapsuleOutputPath(string outputDirArg, string symbolId)
    {
        var safeName = string.Concat(symbolId.Select(c =>
            c is '|' or ':' or '/' or '\\' || Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
        var outputFileName = $"capsule-{safeName}.json";

        if (outputFileName.Length > MaxCapsuleFileNameLength)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(symbolId)))[..16];
            var prefixLength = MaxCapsuleFileNameLength - "capsule-".Length - 1 - hash.Length - ".json".Length;
            outputFileName = $"capsule-{safeName[..prefixLength]}-{hash}.json";
        }

        return Path.Combine(Path.GetFullPath(outputDirArg), outputFileName);
    }

    private static void WriteCapsuleSummary(ContextCapsule capsule, string outputPath)
    {
        Console.WriteLine($"capsule {capsule.Anchor.FullyQualifiedName} ({capsule.Anchor.Kind})");
        Console.WriteLine($"  snapshot: {capsule.Anchor.SnapshotId}  intent: {capsule.Anchor.Intent}  maxHops: {capsule.Anchor.MaxHops}");
        // The two numbers answer different questions and are not interchangeable:
        // content is what --content-budget bounded, delivery is what loading the emitted
        // file costs. The delivery number is always the larger; size a context
        // window from it, never from content.
        Console.WriteLine($"  content tokens:  {capsule.EstimatedTokens}/{capsule.Budget}  (estimatedTokens: the budget basis)");
        Console.WriteLine($"  delivery tokens: ~{capsule.EstimatedArtifactTokens}  (estimatedArtifactTokens: whole emitted file; size the context window from this)");
        Console.WriteLine($"  truncated: {capsule.Truncated}");

        foreach (var (name, count) in new (string, int)[]
                 {
                     ("contracts", capsule.Contracts.Count),
                     ("direct_callees", capsule.DirectCallees.Count),
                     ("direct_callers", capsule.DirectCallers.Count),
                     ("registered_implementations", capsule.RegisteredImplementations.Count),
                     ("relevant_tests", capsule.RelevantTests.Count),
                     ("second_degree_context", capsule.SecondDegreeContext.Count),
                     ("surrounding_source", capsule.SurroundingSource.Count),
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
            // unobservable : refetching returns the same nothing.
            var recoverable = omitted.Reason is "budget_exhausted" or "summarized"
                && ContextAssembler.TierNames.Contains(omitted.Category, StringComparer.Ordinal);
            var continuation = recoverable ? $" : fetch with --tier={omitted.Category}" : string.Empty;
            Console.WriteLine($"  omitted: {omitted.Category} ({omitted.Reason}){continuation}");
        }

        Console.WriteLine($"  written: {outputPath}");
    }

    private static List<string> GetRepeatableArgs(string[] args, string prefix)
        => args.Where(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(arg => arg[prefix.Length..])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToList();
}
