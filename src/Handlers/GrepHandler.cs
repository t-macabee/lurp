using System.Globalization;
using Lurp.Storage;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class GrepHandler
{
    public static void Run(string[] args)
    {
        var queryArg = HandlerBootstrap.GetArgValue(args, "--query=");
        if (string.IsNullOrEmpty(queryArg)) HandlerBootstrap.Fail("ERROR: --query=<term> is required for --mode=grep.");

        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var includeGenerated = args.Contains("--include-generated");
        var ignoreCase = args.Contains("--ignore-case");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var limit = 50;
        if (!string.IsNullOrEmpty(limitArg) && !int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)) HandlerBootstrap.Fail("ERROR: --limit must be an integer.");
        if (limit < 1) HandlerBootstrap.Fail("ERROR: --limit must be a positive integer.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            TextSearchCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = TextSearchCursor.TryDecode(cursorArg);
                if (cursor == null) HandlerBootstrap.Fail("ERROR: --cursor is not a valid cursor for this database.");
            }

            TextSearchPage page;
            try
            {
                page = store.SearchTextPage(queryArg, snapshotId, limit, includeGenerated, ignoreCase, cursor);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var results = new List<object>();
            var summaryLines = new List<string>();
            foreach (var r in page.Items)
            {
                results.Add(new
                {
                    document_path = r.DocumentPath,
                    start_line = r.StartLine,
                    start_column = r.StartColumn,
                    end_line = r.EndLine,
                    end_column = r.EndColumn,
                    line_text = r.LineText
                });
                summaryLines.Add($"{r.DocumentPath}:{r.StartLine}:{r.StartColumn}  {r.LineText}");
            }

            var meta = new
            {
                snapshot_id = snapshotId,
                query = queryArg,
                ignore_case = ignoreCase,
                match_count = page.TotalCount,
                result_count = page.Items.Count,
                next_cursor = page.NextCursor,
                freshness = HandlerBootstrap.FreshnessJson(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var line in summaryLines)
                        Console.WriteLine(line);
                    if (page.NextCursor != null)
                        Console.WriteLine($"-- {page.Items.Count}/{page.TotalCount} match(es); more available (--cursor)");
                    else
                        Console.WriteLine($"-- {page.TotalCount} match(es)");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var result in results)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "result", result }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            snapshot_id = snapshotId,
                            query = queryArg,
                            ignore_case = ignoreCase,
                            results,
                            match_count = page.TotalCount,
                            next_cursor = page.NextCursor,
                            freshness = HandlerBootstrap.FreshnessJson(freshness)
                        },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        });
    }
}
