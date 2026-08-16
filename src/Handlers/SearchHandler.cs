using System.Globalization;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class SearchHandler
{
    public static void Run(string[] args)
    {
        var queryArg = HandlerBootstrap.GetArgValue(args, "--query=");
        if (string.IsNullOrEmpty(queryArg)) HandlerBootstrap.Fail("ERROR: --query=<term> is required for --mode=search.");

        var typeArg = HandlerBootstrap.GetArgValue(args, "--type=") ?? "all";
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var snippetTokensArg = HandlerBootstrap.GetArgValue(args, "--snippet-tokens=");
        var includeGenerated = args.Contains("--include-generated");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var limit = 20;
        if (!string.IsNullOrEmpty(limitArg) && !int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)) HandlerBootstrap.Fail("ERROR: --limit must be an integer.");

        var snippetTokens = 64;
        if (!string.IsNullOrEmpty(snippetTokensArg) && !int.TryParse(snippetTokensArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out snippetTokens)) HandlerBootstrap.Fail("ERROR: --snippet-tokens must be an integer.");

        if (!string.IsNullOrEmpty(cursorArg) && typeArg != "symbol") HandlerBootstrap.Fail("ERROR: --cursor is only supported with --type=symbol.");

        HandlerBootstrap.WithStore<object?>(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            SearchCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = SearchCursor.TryDecode(cursorArg);
                if (cursor == null) HandlerBootstrap.Fail("ERROR: --cursor is not a valid cursor for this database.");
            }

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
                if (typeArg == "symbol")
                {
                    SymbolSearchPage page;
                    try
                    {
                        page = store.SearchSymbolsPage(queryArg, snapshotId, limit, includeGenerated, kindArg, cursor);
                    }
                    catch (ArgumentException ex)
                    {
                        HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                        return null;
                    }

                    foreach (var r in page.Items)
                    {
                        results.Add(new { type = "symbol", symbol_id = r.SymbolId, fully_qualified_name = r.FullyQualifiedName, kind = r.Kind, doc_comment_id = r.DocCommentId });
                        summaryLines.Add($"symbol  {r.SymbolId}  {r.FullyQualifiedName}  ({r.Kind})");
                    }

                    nextCursor = page.NextCursor;
                }
                else
                {
                    var symbolResults = store.SearchSymbols(queryArg, snapshotId, limit, includeGenerated, kindArg);
                    foreach (var r in symbolResults)
                    {
                        results.Add(new { type = "symbol", symbol_id = r.SymbolId, fully_qualified_name = r.FullyQualifiedName, kind = r.Kind, doc_comment_id = r.DocCommentId });
                        summaryLines.Add($"symbol  {r.SymbolId}  {r.FullyQualifiedName}  ({r.Kind})");
                    }
                }
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var meta = new { snapshot_id = snapshotId, query = queryArg, type = typeArg, result_count = results.Count, next_cursor = nextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var line in summaryLines)
                        Console.WriteLine(line);
                    Console.WriteLine($"-- {results.Count} result(s){(nextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var result in results)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", result }, HandlerBootstrap.CompactJson));
                    break;

                // default: Json is the historical default — intentional fallback for OutputMode.Json and future values
                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { snapshot_id = snapshotId, query = queryArg, type = typeArg, results, next_cursor = nextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) },
                        HandlerBootstrap.IndentedJson));
                    break;
            }

            return null;
        });
    }
}