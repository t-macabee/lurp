using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class OutlineHandler
{
    private const int DefaultLimit = 100;

    public static void Run(string[] args)
    {
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        if (string.IsNullOrEmpty(documentArg))
            HandlerBootstrap.Fail("ERROR: --document=<relative-path> is required for --mode=outline.");

        var includeGenerated = args.Contains("--include-generated");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        var limit = DefaultLimit;
        if (!string.IsNullOrEmpty(limitArg))
        {
            if (!int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1)
                HandlerBootstrap.Fail("ERROR: --limit must be a positive integer.");
        }

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            OutlineCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = OutlineCursor.TryDecode(cursorArg);
                if (cursor == null)
                    HandlerBootstrap.Fail("ERROR: --cursor is not a valid continuation token.");
            }

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
                HandlerBootstrap.Fail($"ERROR: Document '{documentArg}' not found in snapshot '{snapshotId}'.");

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var declarations = page.Items.Select(e => new
            {
                symbol_id = e.SymbolId,
                kind = e.Kind,
                fully_qualified_name = e.FullyQualifiedName,
                start_line = e.StartLine,
                end_line = e.EndLine,
                signature_start_line = e.SignatureStartLine,
                name_start_line = e.NameStartLine,
                is_partial = e.IsPartial,
                is_generated = e.IsGenerated
            }).ToList();

            var meta = new
            {
                snapshot_id = snapshotId,
                document = documentArg,
                declaration_count = page.TotalCount,
                next_cursor = page.NextCursor,
                freshness = HandlerBootstrap.FreshnessJson(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var d in declarations)
                        Console.WriteLine($"{d.kind,-12} {d.symbol_id}  {d.fully_qualified_name}  [{d.start_line}:{d.end_line}]  partial={d.is_partial} gen={d.is_generated}");
                    Console.WriteLine($"-- {declarations.Count}/{page.TotalCount} declaration(s){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var d in declarations)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "declaration", declaration = d }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { snapshot_id = snapshotId, document = documentArg, declarations, declaration_count = page.TotalCount, next_cursor = page.NextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        });
    }
}
