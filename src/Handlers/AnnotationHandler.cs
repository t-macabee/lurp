using System.Globalization;
using System.Text.Json;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal static class AnnotationHandler
{
    internal const int DefaultLimit = 100;

    public static void RunAnnotate(string[] args, IOutputSink output)
    {
        var symbolArg = HandlerBootstrap.RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=annotate.");
        var kindArg = HandlerBootstrap.RequireArg(args, "--annotation-kind=", "ERROR: --annotation-kind=<kind> is required for --mode=annotate.");
        var valueArg = HandlerBootstrap.RequireArg(args, "--value=", "ERROR: --value=<text> is required for --mode=annotate.");

        HandlerBootstrap.WithWritableStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId, includeGenerated: true);
            var annotationId = store.SaveUserAnnotation(snapshotId, resolvedSymbol, kindArg!, valueArg!);

            output.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                symbol_id = resolvedSymbol,
                kind = kindArg,
                value = valueArg,
                annotation_id = annotationId
            }, HandlerBootstrap.IndentedJson));
        }, output);
    }

    public static void RunRetractAnnotation(string[] args, IOutputSink output)
    {
        var rawId = HandlerBootstrap.RequireArg(args, "--annotation-id=", "ERROR: --annotation-id=<id> is required for --mode=retract-annotation.");
        if (!long.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var annotationId) || annotationId < 1)
            HandlerBootstrap.Fail("ERROR: --annotation-id must be a positive integer.");

        HandlerBootstrap.WithWritableStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
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

            output.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ok",
                snapshot_id = snapshotId,
                annotation_id = annotationId,
                retracted = true
            }, HandlerBootstrap.IndentedJson));
        }, output);
    }

    public static void RunGetAnnotations(string[] args, IOutputSink output)
    {
        var symbolArg = HandlerBootstrap.GetArgValue(args, "--symbol=");
        var documentArg = HandlerBootstrap.NormalizeDocumentPath(HandlerBootstrap.GetArgValue(args, "--document="));
        var kindArg = HandlerBootstrap.GetArgValue(args, "--kind=");
        var cursorArg = HandlerBootstrap.GetArgValue(args, "--cursor=");
        var outputMode = HandlerBootstrap.ParseOutputMode(args);

        if (!string.IsNullOrEmpty(symbolArg) && !string.IsNullOrEmpty(documentArg))
            HandlerBootstrap.Fail("ERROR: --symbol and --document are mutually exclusive; provide one or neither.");

        var hasSymbol = !string.IsNullOrEmpty(symbolArg);
        var hasDocument = !string.IsNullOrEmpty(documentArg);
        var kindFilter = string.IsNullOrEmpty(kindArg) ? null : kindArg;

        var limit = HandlerBootstrap.ParsePositiveIntArg(args, "--limit=", DefaultLimit);

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            string? resolvedSymbol = null;
            string? normalizedDocument = null;

            if (hasSymbol)
            {
                resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId, includeGenerated: true);
            }
            else if (hasDocument)
            {
                normalizedDocument = documentArg;
                // Distinguish "no annotations here" from "no such document"
                var docs = store.GetDocumentVersionIdsByPath(snapshotId);
                if (!docs.ContainsKey(normalizedDocument!))
                    HandlerBootstrap.Fail($"ERROR: {DocumentMessages.NotFound(normalizedDocument!, snapshotId)}");
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

            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId, output);

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
                freshness = FreshnessJson.Cheap(freshness)
            };

            switch (outputMode)
            {
                case OutputMode.Summary:
                    foreach (var a in annotations)
                        output.WriteLine($"{a.kind,-24} {a.symbol_id}  {a.value}  doc={a.document_path ?? "<null>"}");
                    output.WriteLine($"-- {annotations.Count}/{page.TotalCount} annotation(s){(page.NextCursor != null ? "; more available (--cursor)" : "")}");
                    break;

                case OutputMode.Jsonl:
                    output.WriteLine(JsonSerializer.Serialize(new { type = "meta", meta }, HandlerBootstrap.CompactJson));
                    foreach (var a in annotations)
                        output.WriteLine(JsonSerializer.Serialize(new { type = "annotation", annotation = a }, HandlerBootstrap.CompactJson));
                    break;

                default:
                    output.WriteLine(JsonSerializer.Serialize(
                        new { snapshot_id = snapshotId, symbol_id = resolvedSymbol, document = normalizedDocument, kind = kindFilter, annotations, annotation_count = page.TotalCount, next_cursor = page.NextCursor, freshness = FreshnessJson.Cheap(freshness) },
                        HandlerBootstrap.IndentedJson));
                    break;
            }
        }, output);
    }
}
