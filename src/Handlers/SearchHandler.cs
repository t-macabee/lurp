using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class SearchHandler
{
    internal const int DefaultLimit = 20;
    internal const int DefaultSnippetTokens = 64;

    public static void Run(string[] args, IOutputSink output)
    {
        var queryArg = HandlerBootstrap.RequireFlag(args, FlagCatalog.Query, "search");

        var typeArg = (HandlerBootstrap.GetArgValue(args, "--type=") ?? "all").ToLowerInvariant();
        if (typeArg is not ("all" or "source" or "symbol")) HandlerBootstrap.Fail("ERROR: --type must be one of: all, source, symbol.");
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        string? canonicalKind = null;
        if (!string.IsNullOrEmpty(kindArg) && !DeadCandidateLiveness.TryGetCandidateKind(kindArg, out canonicalKind))
            HandlerBootstrap.Fail($"ERROR: --kind must be one of: {DeadCandidateLiveness.CandidateKindsText} (case-insensitive). Got '{kindArg}'.");
        var includeGenerated = args.Contains("--include-generated");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DefaultLimit);
        var snippetTokens = HandlerBootstrap.ParsePositiveIntArg(args, "--snippet-tokens=", DefaultSnippetTokens);

        if (!string.IsNullOrEmpty(cursorArg) && typeArg != "symbol") HandlerBootstrap.Fail("ERROR: --cursor is only supported with --type=symbol.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var cursor = HandlerBootstrap.DecodeCursor(cursorArg, SearchCursor.TryDecode);

            var results = new List<object>();
            var summaryLines = new List<string>();
            string? nextCursor = null;
            if (typeArg is "source" or "all")
            {
                var sourceResults = store.SearchSource(queryArg, snapshotId, limit, includeGenerated, snippetTokens);
                foreach (var r in sourceResults)
                {
                    results.Add(new { type = "source", document_path = r.DocumentPath, snippet = r.Snippet });
                    summaryLines.Add($"source  {r.DocumentPath}");
                }
            }

            if (typeArg is "symbol" or "all")
            {
                SymbolSearchPage page;
                try
                {
                    page = store.SearchSymbolsPage(
                        queryArg,
                        snapshotId,
                        limit,
                        includeGenerated,
                        canonicalKind,
                        typeArg == "symbol" ? cursor : null);
                }
                catch (ArgumentException ex)
                {
                    HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                    return;
                }

                foreach (var r in page.Items)
                {
                    results.Add(new { type = "symbol", symbol_id = r.SymbolId, fully_qualified_name = r.FullyQualifiedName, kind = r.Kind, doc_comment_id = r.DocCommentId });
                    summaryLines.Add($"symbol  {r.SymbolId}  {r.FullyQualifiedName}  ({r.Kind})");
                }

                if (typeArg == "symbol")
                    nextCursor = page.NextCursor;
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            var meta = new { snapshot_id = snapshotId, query = queryArg, type = typeArg, result_count = results.Count, next_cursor = nextCursor, freshness = FreshnessJson.Cheap(freshness) };

            CliOutputWriter.Write(outputMode, output, () =>
            {
                foreach (var line in summaryLines)
                    output.WriteLine(line);
                output.WriteLine($"-- {results.Count} result(s){(nextCursor != null ? "; more available (--cursor)" : "")}");
            },
            () => new { snapshot_id = snapshotId, query = queryArg, type = typeArg, results, next_cursor = nextCursor, freshness = FreshnessJson.Cheap(freshness) },
            () => meta,
            CliOutputWriter.JsonlItemTypes["search"],
            results);

        }, output);
    }
}
