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

        var outputDirArg = HandlerBootstrap.ResolveOutputDir(args);

        var dbPath = HandlerBootstrap.ResolveDbPath(outputDirArg);

        var store = HandlerBootstrap.OpenStore(dbPath);

        try
        {
            var snapshotId = HandlerBootstrap.ResolveSnapshotId(store, snapshotArg);

            var results = new List<object>();
            if (typeArg == "source" || typeArg == "all")
            {
                var sourceResults = store.SearchSource(queryArg, snapshotId, limit, includeGenerated, snippetTokens);
                foreach (var r in sourceResults)
                    results.Add(new { type = "source", documentPath = r.DocumentPath, snippet = r.Snippet });
            }

            if (typeArg == "symbol" || typeArg == "all")
            {
                var symbolResults = store.SearchSymbols(queryArg, snapshotId, limit, includeGenerated, kindArg);
                foreach (var r in symbolResults)
                    results.Add(new { type = "symbol", symbolId = r.SymbolId, fullyQualifiedName = r.FullyQualifiedName, kind = r.Kind, docCommentId = r.DocCommentId });
            }

            var json = JsonSerializer.Serialize(new { snapshotId, query = queryArg, type = typeArg, results }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        finally
        {
            store.Close();
        }
    }
}
