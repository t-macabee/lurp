using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class AnnotationsTool
{
    private readonly McpSessionContext _session;

    public AnnotationsTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_get_annotations", Title = "Lurp Get Annotations", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Get annotations in the pinned snapshot. Supports three modes: by symbol (" + HandlerBootstrap.AcceptedSymbolFormsText + " More than one match is an error.), by document (solution-relative forward-slashed path; returns annotations whose document_path is that document and annotations of every symbol declared in it, user annotations included), or whole-snapshot when neither is given. Optional kind filter and keyset pagination via limit/cursor (default 100, ordered by annotation_id). annotation_count is the total match count across all pages, not the page size — use next_cursor for pagination.")]
    public string LurpGetAnnotations(
        string? symbol = null,
        string? document = null,
        string? kind = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            var hasSymbol = !string.IsNullOrEmpty(symbol);
            var hasDocument = !string.IsNullOrEmpty(document);

            if (hasSymbol && hasDocument)
                throw new McpProtocolException("symbol and document are mutually exclusive; provide one or neither.", McpErrorCode.InvalidParams);

            var kindFilter = string.IsNullOrEmpty(kind) ? null : kind;

            var limitVal = McpLimits.RequirePositiveAtMost(limit, AnnotationHandler.DefaultLimit, McpLimits.MaxLimit, "limit");

            var cursorObj = McpSessionContext.DecodeCursor(cursor, AnnotationCursor.TryDecode);

            string? resolvedSymbol = null;
            string? normalizedDocument = null;

            if (hasSymbol)
            {
                resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(store, symbol!, snapshotId, includeGenerated: true);
            }
            else if (hasDocument)
            {
                normalizedDocument = HandlerBootstrap.NormalizeDocumentPath(document);
                if (string.IsNullOrEmpty(normalizedDocument))
                    throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);

                // Distinguish "no annotations here" from "no such document"
                var docs = store.GetDocumentVersionIdsByPath(snapshotId);
                if (!docs.ContainsKey(normalizedDocument))
                    throw new McpProtocolException(DocumentMessages.NotFound(normalizedDocument, snapshotId), McpErrorCode.InvalidParams);
            }

            AnnotationPage page;
            try
            {
                page = store.GetAnnotationsPage(snapshotId, resolvedSymbol, normalizedDocument, kindFilter, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                symbol_id = resolvedSymbol,
                document = normalizedDocument,
                kind = kindFilter,
                annotations = page.Items.Select(SurfacePayloads.AnnotationRow).ToList(),
                annotation_count = page.TotalCount,
                next_cursor = page.NextCursor
            };

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}
