using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class AnnotationHandler
{
    private const int DefaultLimit = 100;

    public static void RunAnnotate(string[] args)
    {
        var symbolArg = HandlerBootstrap.RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=annotate.");
        var kindArg = HandlerBootstrap.RequireArg(args, "--annotation-kind=", "ERROR: --annotation-kind=<kind> is required for --mode=annotate.");
        var valueArg = HandlerBootstrap.RequireArg(args, "--value=", "ERROR: --value=<text> is required for --mode=annotate.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var annotation = new AnnotationRecord(symbolArg!, kindArg!, valueArg!);
            store.SaveAnnotations(snapshotId, [annotation]);

            // Read back the freshly inserted row so we can return its surrogate id. The single INSERT per snapshot
            // plus the snapshot_id scope makes MAX(annotation_id) unambiguous within this snapshot even when prior
            // snapshots hold their own copies (CopyAnnotationsToSnapshot allocates fresh ids).
            var inserted = store.GetAnnotations(snapshotId).LastOrDefault(a => a.SymbolId == symbolArg && a.Kind == kindArg && a.Value == valueArg);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                symbol_id = symbolArg,
                kind = kindArg,
                value = valueArg,
                annotation_id = inserted?.AnnotationId ?? 0
            }, HandlerBootstrap.IndentedJson));
        });
    }

    public static void RunRetractAnnotation(string[] args)
    {
        var rawId = HandlerBootstrap.RequireArg(args, "--annotation-id=", "ERROR: --annotation-id=<id> is required for --mode=retract-annotation.");
        if (!long.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var annotationId) || annotationId < 1)
            HandlerBootstrap.Fail("ERROR: --annotation-id must be a positive integer.");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            bool deleted;
            try
            {
                deleted = store.TryRetractAnnotation(snapshotId, annotationId);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            if (!deleted)
                HandlerBootstrap.Fail($"ERROR: annotation_id {annotationId} not found in snapshot '{snapshotId}'.");

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                annotation_id = annotationId,
                retracted = true
            }, HandlerBootstrap.IndentedJson));
        });
    }

    public static void RunGetAnnotations(string[] args)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var limitArg = HandlerBootstrap.GetArgValue(args, "--limit=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        if (!string.IsNullOrEmpty(symbolArg) && !string.IsNullOrEmpty(documentArg))
            HandlerBootstrap.Fail("ERROR: --symbol and --document are mutually exclusive; provide one or neither.");

        var hasSymbol = !string.IsNullOrEmpty(symbolArg);
        var hasDocument = !string.IsNullOrEmpty(documentArg);
        var kindFilter = string.IsNullOrEmpty(kindArg) ? null : kindArg;

        var limit = DefaultLimit;
        if (!string.IsNullOrEmpty(limitArg))
        {
            if (!int.TryParse(limitArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) || limit < 1)
                HandlerBootstrap.Fail("ERROR: --limit must be a positive integer.");
        }

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            string? resolvedSymbol = null;
            string? normalizedDocument = null;

            if (hasSymbol)
            {
                resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId);
            }
            else if (hasDocument)
            {
                normalizedDocument = documentArg;
                // Distinguish "no annotations here" from "no such document"
                var docs = store.GetDocumentVersionIdsByPath(snapshotId);
                if (!docs.ContainsKey(normalizedDocument!))
                    HandlerBootstrap.Fail($"ERROR: Document '{normalizedDocument}' not found in snapshot '{snapshotId}'.");
            }

            AnnotationCursor? cursor = null;
            if (!string.IsNullOrEmpty(cursorArg))
            {
                cursor = AnnotationCursor.TryDecode(cursorArg);
                if (cursor == null)
                    HandlerBootstrap.Fail("ERROR: --cursor is not a valid continuation token.");
            }

            AnnotationPage page;
            try
            {
                page = store.GetAnnotationsPage(snapshotId, resolvedSymbol, normalizedDocument, kindFilter, limit, cursor);
            }
            catch (ArgumentException ex)
            {
                HandlerBootstrap.Fail($"ERROR: {ex.Message}");
                return;
            }

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);

            var annotations = page.Items.Select(a => new
            {
                annotation_id = a.AnnotationId,
                symbol_id = a.SymbolId,
                kind = a.Kind,
                value = a.Value,
                document_path = a.DocumentPath
            }).ToList();

            var meta = new
            {
                snapshot_id = snapshotId,
                symbol_id = resolvedSymbol,
                document = normalizedDocument,
                kind = kindFilter,
                annotation_count = page.TotalCount,
                next_cursor = page.NextCursor,
                freshness = HandlerBootstrap.FreshnessJson(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var a in annotations)
                        Console.WriteLine($"{a.kind,-24} {a.symbol_id}  {a.value}  doc={a.document_path ?? "<null>"}");
                    Console.WriteLine($"-- {annotations.Count}/{page.TotalCount} annotation(s){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    Console.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var a in annotations)
                        Console.WriteLine(JsonSerializer.Serialize(new { type = "annotation", annotation = a }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { snapshot_id = snapshotId, symbol_id = resolvedSymbol, document = normalizedDocument, kind = kindFilter, annotations, annotation_count = page.TotalCount, next_cursor = page.NextCursor, freshness = HandlerBootstrap.FreshnessJson(freshness) },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        });
    }
}
