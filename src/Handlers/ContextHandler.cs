using Lurp.Workspace;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class ContextHandler
{
    // Keep ordinary capsule paths below the legacy Windows MAX_PATH boundary even
    // when the output directory itself is moderately nested.
    private const int MaxCapsuleFileNameLength = 128;

    public static void Run(string[] args, IOutputSink output)
    {
        var affectedProjects = GetRepeatableArgs(args, "--affected-project=");
        var (request, parseError) = ContextRequest.Parse(
            HandlerBootstrap.GetArgValue(args, "--symbol="),
            HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--file=")),
            HandlerBootstrap.GetArgValue(args, "--line="),
            HandlerBootstrap.GetArgValue(args, "--intent="),
            HandlerBootstrap.GetArgValue(args, "--content-budget="),
            HandlerBootstrap.GetArgValue(args, "--max-hops="),
            HandlerBootstrap.GetArgValue(args, "--scope="),
            affectedProjects.Count > 0 ? [.. affectedProjects] : null,
            args.Contains("--include-generated"),
            args.Contains("--completeness-detail"),
            HandlerBootstrap.GetArgValue(args, "--tier="),
            HandlerBootstrap.GetArgValue(args, "--tier-limit="),
            HandlerBootstrap.GetArgValue(args, "--cursor="));
        if (parseError != null)
            HandlerBootstrap.Fail(parseError.FormatCli());

        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args, request!.Tier is not null);
        var quiet = HandlerBootstrap.IsQuiet(args);
        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args, output, Environment.GetEnvironmentVariable);

        HandlerBootstrap.WithStore(args, snapshotArg, (store, snapshotId) =>
        {
            var (binding, bindError) = request.Bind(store, snapshotId);
            if (bindError != null)
                HandlerBootstrap.Fail(bindError.FormatCli());

            if (request.Tier is not null)
            {
                RunTierContinuation(store, args, snapshotId, request, binding!, outputMode, output);
                return;
            }

            var lookup = new ContextLookup(snapshotId, binding!.ResolvedSymbolId, request.File, request.Line);
            var assemblyOptions = new ContextAssemblyOptions(
                request.Intent, request.ContentBudget, request.MaxHops, request.IncludeGenerated,
                request.Scope, request.AffectedProjects, request.CompletenessDetail);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, assemblyOptions, store, store, store);

            HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            WriteCapsuleOutput(capsule, outputDirArg, outputMode, quiet, output);
        }, output);
    }

    /// <summary>
    ///     Serves <c>--tier=&lt;name&gt;</c> (optionally with <c>--cursor=</c>, output): one capsule tier,
    ///     rebuilt outside the capsule budget and paged. This is the action a capsule's
    ///     <c>omitted_tiers: budget_exhausted</c> entry previously admitted to but offered no
    ///     way to take.
    /// </summary>
    private static void RunTierContinuation(
        SqliteIndexStore store, string[] args, string snapshotId, ContextRequest request, ContextBinding binding,
        OutputMode outputMode, IOutputSink output)
    {
        var page = ContextAssembler.BuildTierPage(
            store, store, store, snapshotId, SymbolId.Parse(binding.ResolvedSymbolId!), request.Tier!,
            request.MaxHops, request.IncludeGenerated, binding.Offset, request.TierLimit);

        var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

        var nextCursor = page.HasMore
            ? new SequenceCursor(snapshotId, binding.Fingerprint!, ContextRequest.CursorKind, binding.Offset + page.Items.Count).Encode()
            : null;

        var meta = new
        {
            snapshot_id = snapshotId,
            freshness = FreshnessJson.Cheap(freshness),
            symbol_id = page.SymbolId,
            fully_qualified_name = page.FullyQualifiedName,
            kind = page.Kind,
            tier = page.TierName,
            total_items = page.TotalItems,
            offset = page.Offset,
            next_cursor = nextCursor,
            // The tier is rebuilt in isolation, so no capsule token budget applies here.
            // Saying so keeps this page from being mistaken for a budgeted capsule section.
            budget_applied = false
        };

        CliOutputWriter.Write(outputMode, output, () =>
        {
            output.WriteLine($"tier {page.TierName} of {page.FullyQualifiedName} ({page.Kind})");
            output.WriteLine($"  items: {page.TotalItems} total, {page.Items.Count} in this page (offset {page.Offset})");
            foreach (var item in page.Items)
            {
                // An item with no source location (a Route placeholder) has no path:line to print.
                var location = item.DocumentPath is null ? "" : $"  {item.DocumentPath}:{item.StartLine}";
                output.WriteLine($"  {item.FullyQualifiedName}  [{item.EdgeKind}/{item.Provenance}]{location}");
            }
            if (nextCursor != null)
                output.WriteLine("  more available: re-run with --cursor=<next_cursor from --output=json>.");
        },
        () => new
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
            items = page.Items
        },
        () => meta,
        CliOutputWriter.JsonlItemTypes["context"],
        page.Items);
    }

    internal static bool TryParseIntent(string value, out ContextIntent intent)
    {
        switch (value.ToLowerInvariant())
        {
            case "inspect":
                intent = ContextIntent.Inspect;
                return true;
            case "modify":
                intent = ContextIntent.Modify;
                return true;
            case "diagnose":
                intent = ContextIntent.Diagnose;
                return true;
            default:
                intent = ContextIntent.Inspect;
                return false;
        }
    }

    internal static void WriteCapsuleOutput(ContextCapsule capsule, string outputDirArg, OutputMode outputMode, bool quiet, IOutputSink output)
    {
        var json = ContextCapsuleJson.Serialize(capsule);
        var outputPath = GetCapsuleOutputPath(outputDirArg, capsule.Anchor.SymbolId);
        // Unique per-write temp name: two concurrent capsule runs for the same
        // symbol must not overwrite each other's scratch file before the move.
        var tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, outputPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        // The capsule file is always written; only the stdout echo is optional. --quiet
        // exists because the default duplicates an 81 KB artifact into the caller's
        // context for no gain when the file path is all they needed.
        if (quiet)
        {
            output.WriteLine(outputPath);
            return;
        }

        if (outputMode == OutputMode.Summary)
        {
            WriteCapsuleSummary(capsule, outputPath, output);
            return;
        }

        output.WriteLine(json);
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

    private static void WriteCapsuleSummary(ContextCapsule capsule, string outputPath, IOutputSink output)
    {
        output.WriteLine($"capsule {capsule.Anchor.FullyQualifiedName} ({capsule.Anchor.Kind})");
        output.WriteLine($"  snapshot: {capsule.Anchor.SnapshotId}  intent: {capsule.Anchor.Intent}  maxHops: {capsule.Anchor.MaxHops}");
        // The two numbers answer different questions and are not interchangeable:
        // content is what --content-budget bounded, delivery is what loading the emitted
        // file costs. The delivery number is always the larger; size a context
        // window from it, never from content.
        output.WriteLine($"  content tokens:  {capsule.EstimatedTokens}/{capsule.Budget}  (estimated_tokens: the budget basis)");
        output.WriteLine($"  delivery tokens: ~{capsule.EstimatedArtifactTokens}  (estimated_artifact_tokens: whole emitted file; size the context window from this)");
        output.WriteLine($"  truncated: {capsule.Truncated}");

        foreach (var (name, count) in new (string, int)[]
                 {
                     ("contracts", capsule.Contracts.Count),
                     ("direct_callees", capsule.DirectCallees.Count),
                     ("direct_callers", capsule.DirectCallers.Count),
                     ("registered_implementations", capsule.RegisteredImplementations.Count),
                     ("relevant_tests", capsule.RelevantTests.Count),
                     ("second_degree_context", capsule.SecondDegreeContext.Count),
                     ("surrounding_source", capsule.SurroundingSource.Count)
                 })
            output.WriteLine($"  {name,-28} {count}");

        output.WriteLine($"  incoming_witness_paths: {capsule.IncomingPaths.Count}  outgoing_witness_paths: {capsule.OutgoingPaths.Count}  uncertainties: {capsule.Uncertainties.Count}");

        foreach (var omitted in capsule.OmittedTiers)
        {
            // Only budget-bounded tiers have anything to recover. Offering the
            // continuation for an "empty" or "unresolved" tier would suggest the
            // content is behind a budget when it is either proved absent or
            // unobservable : refetching returns the same nothing.
            var recoverable = omitted.Reason is "budget_exhausted" or "summarized"
                              && ContextAssembler.TierNames.Contains(omitted.Category, StringComparer.Ordinal);
            var continuation = recoverable ? $" : fetch with --tier={omitted.Category}" : string.Empty;
            output.WriteLine($"  omitted: {omitted.Category} ({omitted.Reason}){continuation}");
        }

        output.WriteLine($"  written: {outputPath}");
    }

    private static List<string> GetRepeatableArgs(string[] args, string prefix)
    {
        return [.. args.Where(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(arg => arg[prefix.Length..])
            .Where(static value => !string.IsNullOrWhiteSpace(value))];
    }
}