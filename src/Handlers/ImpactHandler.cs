using Lurp.Workspace;
using System.Globalization;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class ImpactHandler
{
    private const string CursorKind = "impact";
    private const int DefaultMaxPaths = 50;

    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg)) HandlerBootstrap.Fail("ERROR: --symbol=<symbol-id> is required for --mode=impact.");

        var directionArg = HandlerBootstrap.GetArgValue(args, "--direction=") ?? "downstream";
        var direction = directionArg.ToLowerInvariant() switch
        {
            "downstream" => ImpactDirection.Downstream,
            "upstream" => ImpactDirection.Upstream,
            _ => throw new ArgumentException($"Invalid direction '{directionArg}'. Use 'upstream' or 'downstream'.")
        };

        var maxDepthArg = HandlerBootstrap.GetArgValue(args, "--max-depth=");
        var maxDepth = 3;
        if (!string.IsNullOrEmpty(maxDepthArg) && (!int.TryParse(maxDepthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth) || maxDepth < 1)) HandlerBootstrap.Fail("ERROR: --max-depth must be a positive integer.");

        var kindsArg = HandlerBootstrap.GetArgValue(args, "--kinds=");
        HashSet<string>? allowedKinds = !string.IsNullOrEmpty(kindsArg)
            ? [.. kindsArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

        var provenanceArg = HandlerBootstrap.GetArgValue(args, "--provenance=");
        HashSet<string>? allowedProvenance = !string.IsNullOrEmpty(provenanceArg)
            ? [.. provenanceArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : null;

        var maxPaths = HandlerBootstrap.ParsePositiveIntArg(args, "--max-paths=", DefaultMaxPaths);
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId);

            var fingerprint = SequenceCursor.ComputeFingerprint(
                resolvedSymbolId,
                direction.ToString(),
                maxDepth.ToString(CultureInfo.InvariantCulture),
                kindsArg,
                provenanceArg);
            var cursor = HandlerBootstrap.ResolveSequenceCursor(args, snapshotId, fingerprint, CursorKind);
            var offset = cursor?.Offset ?? 0;

            var traverser = new ImpactTraverser(store, snapshotId, store);
            var traced = traverser.TraceImpact(resolvedSymbolId, direction, allowedKinds, allowedProvenance, maxDepth);

            var paged = ImpactPaging.Page(traced, offset, maxPaths, snapshotId, fingerprint, CursorKind);

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var meta = new
            {
                snapshot_id = snapshotId,
                freshness = HandlerBootstrap.FreshnessJson(freshness),
                symbol_id = resolvedSymbolId,
                direction = direction == ImpactDirection.Downstream ? "downstream" : "upstream",
                max_depth = maxDepth,
                path_count_total = paged.TotalPathCount,
                offset,
                groups = paged.Groups,
                truncated = paged.Truncated
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    // Summary rows are read by humans, so they lead with names; the full
                    // docCommentId|assemblyIdentity strings stay in --output=json, which is
                    // what a consumer feeds back into --symbol=.
                    var displayName = MakeNameResolver(store, snapshotId);
                    WriteSummary(displayName(meta.symbol_id!), meta.symbol_id!, meta.direction, paged.TotalPathCount, offset, paged.PathJson.Count, paged.Groups.Count,
                        paged.Groups.Select(group => ($"{displayName(group.first_hop_source_symbol_id)} → {displayName(group.first_hop_target_symbol_id)} [{group.edge_kind}]", group.path_count)),
                        paged.Truncated);
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var path in paged.PathJson)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "path", path }, HandlerBootstrap.CompactJson));
                    break;

                // default: Json is the historical default — intentional fallback for OutputMode.Json and future values
                default:
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        meta.snapshot_id,
                        meta.freshness,
                        meta.symbol_id,
                        meta.direction,
                        meta.max_depth,
                        meta.path_count_total,
                        meta.offset,
                        meta.groups,
                        meta.truncated,
                        paths = paged.PathJson
                    }, HandlerBootstrap.IndentedJson));
                    break;
            }

        });
    }

    /// <summary>
    ///     Maps a symbol ID to a display name, memoized because a page of paths repeats the
    ///     same first-hop endpoints many times over. Falls back to the doc-comment part of the
    ///     ID when a symbol is not in the snapshot (external targets carry no indexed record),
    ///     so a row never degrades to blank.
    /// </summary>
    private static Func<string, string> MakeNameResolver(SqliteIndexStore store, string snapshotId)
    {
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        return symbolId =>
        {
            if (cache.TryGetValue(symbolId, out var cached))
                return cached;

            var fqn = store.GetSymbolInfo(symbolId, snapshotId)?.FullyQualifiedName;
            var name = string.IsNullOrEmpty(fqn)
                ? DocCommentPart(symbolId)
                : fqn.StartsWith("global::", StringComparison.Ordinal)
                    ? fqn["global::".Length..]
                    : fqn;

            cache[symbolId] = name;
            return name;
        };
    }

    /// <summary>The <c>docCommentId</c> half of a symbol ID, without the assembly identity.</summary>
    private static string DocCommentPart(string symbolId)
    {
        var pipe = symbolId.IndexOf('|');
        return pipe > 0 ? symbolId[..pipe] : symbolId;
    }

    private static void WriteSummary(string symbolName, string symbolId, string direction, int total, int offset, int returned, int groupCount,
        IEnumerable<(string Label, int Count)> groupLines, object? truncated)
    {
        Console.WriteLine($"impact {direction} of {symbolName}");
        Console.WriteLine($"  symbol: {symbolId}");
        Console.WriteLine($"  paths: {total} total, {returned} in this page (offset {offset}); {groupCount} distinct first hop(s)");
        foreach (var (label, count) in groupLines)
            Console.WriteLine($"  {count,5}  {label}");

        if (truncated is not null)
            Console.WriteLine("  truncated: pass --cursor=<token from --output=json> to continue.");
    }
}