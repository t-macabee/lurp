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
            Console.Error.WriteLine("ERROR: --query=<term> is required for --mode=search.");
            Environment.Exit(1);
        }

        var typeArg = HandlerBootstrap.GetArgValue(args, "--type=") ?? "all";
        var snapshotArg = HandlerBootstrap.GetArgValue(args, "--snapshot=");
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var snippetTokensArg = HandlerBootstrap.GetArgValue(args, "--snippet-tokens=");
        var includeGenerated = args.Contains("--include-generated");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");

        int limit = 20;
        if (!string.IsNullOrEmpty(limitArg) && !int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
        {
            Console.Error.WriteLine("ERROR: --limit must be an integer.");
            Environment.Exit(1);
        }

        int snippetTokens = 64;
        if (!string.IsNullOrEmpty(snippetTokensArg) && !int.TryParse(snippetTokensArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out snippetTokens))
        {
            Console.Error.WriteLine("ERROR: --snippet-tokens must be an integer.");
            Environment.Exit(1);
        }

        if (!string.IsNullOrEmpty(cursorArg) && typeArg != "symbol")
        {
            Console.Error.WriteLine("ERROR: --cursor is only supported with --type=symbol.");
            Environment.Exit(1);
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
                    Console.Error.WriteLine("ERROR: --cursor is not a valid cursor for this database.");
                    Environment.Exit(1);
                }
            }

            var results = new List<object>();
            string? nextCursor = null;
            if (typeArg == "source" || typeArg == "all")
            {
                var sourceResults = store.SearchSource(queryArg, snapshotId, limit, includeGenerated, snippetTokens);
                foreach (var r in sourceResults)
                    results.Add(new { type = "source", documentPath = r.DocumentPath, snippet = r.Snippet });
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
                        Console.Error.WriteLine($"ERROR: {ex.Message}");
                        Environment.Exit(1);
                        return;
                    }
                    foreach (var r in page.Items)
                        results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
                    nextCursor = page.NextCursor;
                }
                else
                {
                    var symbolResults = store.SearchSymbols(queryArg, snapshotId, limit, includeGenerated, kindArg);
                    foreach (var r in symbolResults)
                        results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
                }
            }

            var freshness = HandlerBootstrap.ComputeFreshnessStamp(store, snapshotId, args);
            HandlerBootstrap.EnforceRequireFresh(args, freshness);
            HandlerBootstrap.PrintFreshnessLine(freshness);

            var json = JsonSerializer.Serialize(new { snapshotId, query = queryArg, type = typeArg, results, nextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
