using Lurp.Workspace;
using System.Globalization;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class ImpactHandler
{
    internal const string CursorKind = "impact";
    internal const int DefaultLimit = 50;
    internal const int DefaultMaxDepth = 3;

    internal static bool TryParseDirection(string value, out ImpactDirection direction)
    {
        switch (value.ToLowerInvariant())
        {
            case "downstream":
                direction = ImpactDirection.Downstream;
                return true;
            case "upstream":
                direction = ImpactDirection.Upstream;
                return true;
            default:
                direction = ImpactDirection.Downstream;
                return false;
        }
    }

    internal static readonly IReadOnlyList<string> EdgeKindNames = Enum.GetNames<EdgeKind>();

    /// <summary>
    ///     The edge kinds an <c>upstream</c> trace follows when the caller names none: every
    ///     kind except the three whose backwards traversal answers "what does this belong to"
    ///     rather than "what depends on this" (<c>Declares</c> member → type, <c>Contains</c>
    ///     nested type → outer type, <c>TestedBy</c> test → every production symbol it covers).
    ///     <c>downstream</c> keeps every kind. An explicit <c>--kinds=</c> / <c>kinds</c> replaces
    ///     this set.
    /// </summary>
    internal static readonly IReadOnlySet<string> UpstreamDefaultKinds = EdgeKindNames
        .Where(name => name is not nameof(EdgeKind.Declares) and not nameof(EdgeKind.Contains) and not nameof(EdgeKind.TestedBy))
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    ///     Why a name filter did not parse; see <see cref="TryParseNameFilter" />. <see cref="Parsed" />
    ///     with a null <c>canonical</c> means the input held no part at all (for example an empty
    ///     value), which no caller may treat as "no filter".
    /// </summary>
    internal enum NameFilterParseOutcome
    {
        Parsed,

        /// <summary>A comma-separated part was empty (<c>Calls,</c>, <c>,Calls</c>, <c>""</c>).</summary>
        EmptyItem,

        /// <summary>A non-empty part matched no known name.</summary>
        UnknownName
    }

    /// <summary>
    ///     Parses a repeatable, comma-separated name filter (CLI <c>--kinds=</c> / <c>--provenance=</c>,
    ///     MCP <c>kinds</c> / <c>provenance</c>). Each part is matched against <paramref name="known" />
    ///     case-insensitively and replaced with its canonical spelling; input order is kept and
    ///     duplicates are dropped. The first unknown part is reported as
    ///     <see cref="NameFilterParseOutcome.UnknownName" /> (with <paramref name="unknown" /> set) and
    ///     the first empty part of a non-empty filter as <see cref="NameFilterParseOutcome.EmptyItem" />.
    ///     When the input holds no name at all (an empty value, an empty array, or only separators)
    ///     <paramref name="canonical" /> is null; the caller reports that instead of running without a
    ///     filter, because a wrong filter must not read as a real answer.
    /// </summary>
    internal static NameFilterParseOutcome TryParseNameFilter(IEnumerable<string> values, IReadOnlyList<string> known, out IReadOnlyList<string>? canonical, out string? unknown)
    {
        canonical = null;
        unknown = null;

        // The whole filter is blank: nothing was named at all. That is the callers'
        // "must name at least one" case, not an empty item next to a real name.
        var parts = values.SelectMany(value => value.Split(',', StringSplitOptions.TrimEntries)).ToList();
        if (parts.All(static part => part.Length == 0))
            return NameFilterParseOutcome.Parsed;

        var result = new List<string>();
        foreach (var part in parts)
        {
            if (part.Length == 0)
                return NameFilterParseOutcome.EmptyItem;

            var match = known.FirstOrDefault(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                unknown = part;
                return NameFilterParseOutcome.UnknownName;
            }

            if (!result.Contains(match, StringComparer.Ordinal))
                result.Add(match);
        }

        canonical = result;
        return NameFilterParseOutcome.Parsed;
    }

    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        if (string.IsNullOrEmpty(symbolArg)) HandlerBootstrap.Fail("ERROR: --symbol=<symbol-id> is required for --mode=impact.");

        var directionArg = HandlerBootstrap.GetArgValue(args, "--direction=") ?? "downstream";
        if (!TryParseDirection(directionArg, out var direction))
        {
            HandlerBootstrap.Fail($"ERROR: Invalid direction '{directionArg}'. Use 'upstream' or 'downstream'.");
            return;
        }

        var maxDepthArg = HandlerBootstrap.GetArgValue(args, "--max-depth=");
        var maxDepth = DefaultMaxDepth;
        if (!string.IsNullOrEmpty(maxDepthArg) && (!int.TryParse(maxDepthArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth) || maxDepth < 1)) HandlerBootstrap.Fail("ERROR: --max-depth must be a positive integer.");

        var kindsArg = HandlerBootstrap.GetArgValue(args, "--kinds=");
        IReadOnlyList<string>? canonicalKinds = null;
        if (kindsArg is not null)
        {
            var kindsOutcome = TryParseNameFilter([kindsArg], EdgeKindNames, out canonicalKinds, out var unknownKind);
            if (kindsOutcome == NameFilterParseOutcome.EmptyItem) HandlerBootstrap.Fail("ERROR: --kinds contains an empty item.");
            if (kindsOutcome == NameFilterParseOutcome.UnknownName) HandlerBootstrap.Fail($"ERROR: Unknown edge kind '{unknownKind}' in --kinds. Use one of: {string.Join(", ", EdgeKindNames)} (case-insensitive).");
            if (canonicalKinds is null) HandlerBootstrap.Fail($"ERROR: --kinds must name at least one edge kind. Use one of: {string.Join(", ", EdgeKindNames)} (case-insensitive).");
        }

        var provenanceArg = HandlerBootstrap.GetArgValue(args, "--provenance=");
        IReadOnlyList<string>? canonicalProvenance = null;
        if (provenanceArg is not null)
        {
            var provenanceOutcome = TryParseNameFilter([provenanceArg], Provenance.All, out canonicalProvenance, out var unknownProvenance);
            if (provenanceOutcome == NameFilterParseOutcome.EmptyItem) HandlerBootstrap.Fail("ERROR: --provenance contains an empty item.");
            if (provenanceOutcome == NameFilterParseOutcome.UnknownName) HandlerBootstrap.Fail($"ERROR: Unknown provenance '{unknownProvenance}' in --provenance. Use one of: {string.Join(", ", Provenance.All)} (case-insensitive).");
            if (canonicalProvenance is null) HandlerBootstrap.Fail($"ERROR: --provenance must name at least one provenance. Use one of: {string.Join(", ", Provenance.All)} (case-insensitive).");
        }

        HashSet<string>? allowedKinds = canonicalKinds is not null
            ? new HashSet<string>(canonicalKinds, StringComparer.Ordinal)
            : direction == ImpactDirection.Upstream
                ? new HashSet<string>(UpstreamDefaultKinds, StringComparer.Ordinal)
                : null;
        HashSet<string>? allowedProvenance = canonicalProvenance is null ? null : new HashSet<string>(canonicalProvenance, StringComparer.Ordinal);

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DefaultLimit);
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId);

            var fingerprint = SequenceCursor.ComputeFingerprint(
                resolvedSymbolId,
                direction.ToString(),
                maxDepth.ToString(CultureInfo.InvariantCulture),
                canonicalKinds is null ? null : string.Join(",", canonicalKinds),
                canonicalProvenance is null ? null : string.Join(",", canonicalProvenance),
                "symbols");
            var cursor = HandlerBootstrap.ResolveSequenceCursor(args, snapshotId, fingerprint, CursorKind);
            var offset = cursor?.Offset ?? 0;

            var reachability = new ImpactReachability(store, snapshotId, store);
            var traced = reachability.Trace(resolvedSymbolId, direction, allowedKinds, allowedProvenance, maxDepth);

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var response = ImpactPaging.BuildSymbolsResponse(
                snapshotId,
                FreshnessJson.Cheap(freshness),
                resolvedSymbolId,
                direction,
                maxDepth,
                traced,
                offset,
                limit,
                fingerprint,
                CursorKind);

            switch (outputMode)
            {
                case OutputMode.Summary:
                    // Summary rows are read by humans, so they lead with names; the full
                    // docCommentId|assemblyIdentity strings stay in --output=json, which is
                    // what a consumer feeds back into --symbol=.
                    var displayName = MakeNameResolver(store, snapshotId);
                    WriteSummary(displayName(response.symbol_id), response.direction, response.symbol_count_total, response.symbols.Count, offset,
                        response.groups.Count, response.frontier_count,
                        response.groups.Select(group => ($"{displayName(group.first_hop_source_symbol_id)} → {displayName(group.first_hop_target_symbol_id)} [{group.edge_kind}]", group.symbol_count)),
                        response.truncated);
                    break;

                case OutputMode.Jsonl:
                    WriteJsonl(response);
                    break;

                // default: Json is the historical default — intentional fallback for OutputMode.Json and future values
                default:
                    Console.WriteLine(JsonSerializer.Serialize(response, HandlerBootstrap.IndentedJson));
                    break;
            }

        });
    }

    /// <summary>
    ///     Maps a symbol ID to a display name, memoized because a page of groups repeats the
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
        => SymbolId.DocCommentIdOf(symbolId);

    private static void WriteSummary(string symbolName, string direction, int total, int returned, int offset, int groupCount, int frontierCount,
        IEnumerable<(string Label, int Count)> groupLines, object? truncated)
    {
        Console.WriteLine($"impact {direction} of {symbolName}");
        Console.WriteLine($"  symbols: {total} total, {returned} in this page (offset {offset}); {groupCount} distinct first hop(s); {frontierCount} at max depth");
        foreach (var (label, count) in groupLines)
            Console.WriteLine($"  {count,5}  {label}");

        if (truncated is not null)
            Console.WriteLine("  truncated: pass --cursor=<token from --output=json> to continue.");
    }

    private static void WriteJsonl(ImpactResponse response)
    {
        // The meta line is the response without the per-symbol rows: a streaming
        // consumer reads the header first and then one symbol per line.
        var meta = JsonSerializer.SerializeToNode(response, HandlerBootstrap.CompactJson)!.AsObject();
        meta.Remove("symbols");
        Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
        foreach (var symbol in response.symbols)
            Console.WriteLine(JsonSerializer.Serialize(new { type = "symbol", symbol }, HandlerBootstrap.CompactJson));
    }
}