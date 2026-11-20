using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class GrepHandler
{
    internal const int DefaultLimit = 50;

    public static void Run(string[] args, IOutputSink output)
    {
        var queryArg = HandlerBootstrap.RequireFlag(args, FlagCatalog.Query, "grep");

        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var includeGenerated = args.Contains("--include-generated");
        var ignoreCase = args.Contains("--ignore-case");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DefaultLimit);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var cursor = HandlerBootstrap.DecodeCursor(cursorArg, TextSearchCursor.TryDecode);

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

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

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
                freshness = FreshnessJson.Cheap(freshness)
            };

            CliOutputWriter.Write(outputMode, output, () =>
            {
                foreach (var line in summaryLines)
                    output.WriteLine(line);
                if (page.NextCursor != null)
                    output.WriteLine($"-- {page.Items.Count}/{page.TotalCount} match(es); more available (--cursor)");
                else
                    output.WriteLine($"-- {page.TotalCount} match(es)");
            },
            () => new
            {
                snapshot_id = snapshotId,
                query = queryArg,
                ignore_case = ignoreCase,
                results,
                match_count = page.TotalCount,
                next_cursor = page.NextCursor,
                freshness = FreshnessJson.Cheap(freshness)
            },
            () => meta,
            CliOutputWriter.JsonlItemTypes["grep"],
            results);
        }, output);
    }
}
