using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class SearchHandler
{
    public static void Run(string[] args)
    {
        var queryArg = HandlerBootstrap.GetArgValue(args, "--query=");
        if (string.IsNullOrEmpty(queryArg))
        {
            HandlerBootstrap.Fail("ERROR: --query=<term> is required for --mode=search.");
        }

        var typeArg = HandlerBootstrap.GetArgValue(args, "--type=") ?? "all";
        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var snippetTokensArg = HandlerBootstrap.GetArgValue(args, "--snippet-tokens=");
        var includeGenerated = args.Contains("--include-generated");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        int limit = 20;
        if (!string.IsNullOrEmpty(limitArg) && !int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
        {
            HandlerBootstrap.Fail("ERROR: --limit must be an integer.");
        }

        int snippetTokens = 64;
        if (!string.IsNullOrEmpty(snippetTokensArg) && !int.TryParse(snippetTokensArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out snippetTokens))
        {
            HandlerBootstrap.Fail("ERROR: --snippet-tokens must be an integer.");
        }

        if (!string.IsNullOrEmpty(cursorArg) && typeArg != "symbol")
        {
            HandlerBootstrap.Fail("ERROR: --cursor is only supported with --type=symbol.");
        }

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            SearchCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = SearchCursor.TryDecode(cursorArg);
                if (cursor == null)
                {
                    HandlerBootstrap.Fail("ERROR: --cursor is not a valid cursor for this database.");
                }
            }

            var results = new List<object>();
            // Kept alongside the JSON results rather than derived from them: the results
            // are anonymous types of two different shapes, and reflecting over them to
            // render a line would be a worse contract than writing it where the shape is known.
            var summaryLines = new List<string>();
            string? nextCursor = null;
            if (typeArg == "source" || typeArg == "all")
            {
                var sourceResults = store.SearchSource(queryArg, snapshotId, limit, includeGenerated, snippetTokens);
                foreach (var r in sourceResults)
                {
                    results.Add(new { type = "source", documentPath = r.DocumentPath, snippet = r.Snippet });
                    summaryLines.Add($"source  {r.DocumentPath}");
                }
            }

            if (typeArg == "symbol" || typeArg == "all")
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
                        return;
                    }
                    foreach (var r in page.Items)
                    {
                        results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
                        summaryLines.Add($"symbol  {r.SymbolId}  {r.FullyQualifiedName}  ({r.Kind})");
                    }
                    nextCursor = page.NextCursor;
                }
                else
                {
                    var symbolResults = store.SearchSymbols(queryArg, snapshotId, limit, includeGenerated, kindArg);
                    foreach (var r in symbolResults)
                    {
                        results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
                        summaryLines.Add($"symbol  {r.SymbolId}  {r.FullyQualifiedName}  ({r.Kind})");
                    }
                }
            }

            var freshness = HandlerBootstrap.ComputeFreshnessStamp(store, store, snapshotId, args);
            HandlerBootstrap.EnforceRequireFresh(args, freshness);
            HandlerBootstrap.PrintFreshnessLine(args, freshness);

            var meta = new { snapshotId, query = queryArg, type = typeArg, resultCount = results.Count, nextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) };

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

                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { snapshotId, query = queryArg, type = typeArg, results, nextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        }
        finally
        {
            store.Close();
        }
    }
}
