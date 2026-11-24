using Lurp.Workspace;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.Handlers;

internal static class ImpactHandler
{
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

    public static void Run(string[] args, IOutputSink output)
    {
        var kindsArg = HandlerBootstrap.GetArgValue(args, "--kinds=");
        var provenanceArg = HandlerBootstrap.GetArgValue(args, "--provenance=");
        var (request, parseError) = ImpactRequest.Parse(
            HandlerBootstrap.GetArgValue(args, "--symbol="),
            HandlerBootstrap.GetArgValue(args, "--direction="),
            kindsArg is null ? null : [kindsArg],
            provenanceArg is null ? null : [provenanceArg],
            HandlerBootstrap.GetArgValue(args, "--max-depth="),
            HandlerBootstrap.GetArgValue(args, "--limit="),
            HandlerBootstrap.GetArgValue(args, "--cursor="));
        if (parseError != null)
            HandlerBootstrap.Fail(parseError.FormatCli());

        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var (binding, bindError) = request!.Bind(store, snapshotId);
            if (bindError != null)
                HandlerBootstrap.Fail(bindError.FormatCli());

            var reachability = new ImpactReachability(store, snapshotId, store);
            var traced = reachability.Trace(binding!.ResolvedSymbolId, request.Direction, request.MaxDepth, request.AllowedKinds, request.AllowedProvenance);
            var semanticCauses = store.GetSemanticChangesForSymbol(snapshotId, binding.ResolvedSymbolId);

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            var response = ImpactPaging.BuildSymbolsResponse(
                snapshotId,
                FreshnessJson.Cheap(freshness),
                binding.ResolvedSymbolId,
                request.Direction,
                request.MaxDepth,
                traced,
                reachability,
                semanticCauses,
                binding.Offset,
                request.Limit,
                binding.Fingerprint,
                ImpactRequest.CursorKind);

            CliOutputWriter.Write(outputMode, output, () =>
            {
                // Summary rows are read by humans, so they lead with names; the full
                // docCommentId|assemblyIdentity strings stay in --output=json, which is
                // what a consumer feeds back into --symbol=.
                var displayName = MakeNameResolver(store, snapshotId);
                WriteSummary(displayName(response.symbol_id), response.direction, response.symbol_count_total, response.symbols.Count, binding.Offset,
                    response.groups.Count, response.frontier_count,
                    response.groups.Select(group => ($"{displayName(group.first_hop_source_symbol_id)} → {displayName(group.first_hop_target_symbol_id)} [{group.edge_kind}]", group.symbol_count)),
                    response.truncated, output);
            },
            () => response,
            () => BuildJsonlMeta(response),
            CliOutputWriter.JsonlItemTypes["impact"],
            response.symbols);

        }, output);
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
        IEnumerable<(string Label, int Count)> groupLines, object? truncated, IOutputSink output)
    {
        output.WriteLine($"impact {direction} of {symbolName}");
        output.WriteLine($"  symbols: {total} total, {returned} in this page (offset {offset}); {groupCount} distinct first hop(s); {frontierCount} at max depth");
        foreach (var (label, count) in groupLines)
            output.WriteLine($"  {count,5}  {label}");

        if (truncated is not null)
            output.WriteLine("  truncated: pass --cursor=<token from --output=json> to continue.");
    }

    private static JsonObject BuildJsonlMeta(ImpactResponse response)
    {
        // The meta record is the response without the per-symbol rows.
        var meta = JsonSerializer.SerializeToNode(response, HandlerBootstrap.CompactJson)!.AsObject();
        meta.Remove("symbols");
        return meta;
    }
}
