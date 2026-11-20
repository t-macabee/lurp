using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class OutlineHandler
{
    internal const int DefaultLimit = 100;

    public static void Run(string[] args, IOutputSink output)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.RequireFlag(args, FlagCatalog.Document, "outline"));

        var includeGenerated = args.Contains("--include-generated");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DefaultLimit);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var cursor = HandlerBootstrap.DecodeCursor(cursorArg, OutlineCursor.TryDecode);

            DeclarationOutlinePage? page;
            try
            {
                page = store.GetDeclarationsOutline(documentArg, snapshotId, includeGenerated, limit, cursor);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            if (page == null)
                HandlerBootstrap.Fail($"ERROR: {DocumentMessages.NotFound(documentArg, snapshotId)}");

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

            var declarations = page.Items.Select(SurfacePayloads.OutlineDeclaration).ToList();

            var meta = new
            {
                snapshot_id = snapshotId,
                document = documentArg,
                declaration_count = page.TotalCount,
                next_cursor = page.NextCursor,
                freshness = FreshnessJson.Cheap(freshness)
            };

            CliOutputWriter.Write(outputMode, output, () =>
            {
                foreach (var d in declarations)
                    output.WriteLine($"{d.kind,-12} {d.symbol_id}  {d.fully_qualified_name}  [{d.start_line}:{d.end_line}]  partial={d.is_partial} gen={d.is_generated}");
                output.WriteLine($"-- {declarations.Count}/{page.TotalCount} declaration(s){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
            },
            () => new { snapshot_id = snapshotId, document = documentArg, declarations, declaration_count = page.TotalCount, next_cursor = page.NextCursor, freshness = FreshnessJson.Cheap(freshness) },
            () => meta,
            CliOutputWriter.JsonlItemTypes["outline"],
            declarations);
        }, output);
    }
}
