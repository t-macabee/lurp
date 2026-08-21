using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
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
    [Description("Get annotations in the pinned snapshot. Supports three modes: by symbol (resolved via docCommentId|assemblyIdentity, bare T: docCommentId, or FQN), by document (git-relative forward-slashed path), or whole-snapshot when neither is given. Optional kind filter and keyset pagination via limit/cursor (default 100, ordered by annotation_id). annotation_count is the total match count across all pages, not the page size — use next_cursor for pagination. Annotations with document_path IS NULL (user-authored via lurp_annotate) are unreachable via document filter.")]
    public string LurpGetAnnotations(
        string? symbol = null,
        string? document = null,
        string? kind = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            var hasSymbol = !string.IsNullOrEmpty(symbol);
            var hasDocument = !string.IsNullOrEmpty(document);

            if (hasSymbol && hasDocument)
                throw new McpProtocolException("symbol and document are mutually exclusive; provide one or neither.", McpErrorCode.InvalidParams);

            var kindFilter = string.IsNullOrEmpty(kind) ? null : kind;

            var limitVal = limit ?? 100;
            if (limitVal < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);

            AnnotationCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = AnnotationCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

            string? resolvedSymbol = null;
            string? normalizedDocument = null;

            if (hasSymbol)
            {
                resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(_session.Store, symbol!, snapshotId);
            }
            else if (hasDocument)
            {
                normalizedDocument = HandlerBootstrap.NormalizeDocumentPath(document);
                if (string.IsNullOrEmpty(normalizedDocument))
                    throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);

                // Distinguish "no annotations here" from "no such document"
                var docs = _session.Store.GetDocumentVersionIdsByPath(snapshotId);
                if (!docs.ContainsKey(normalizedDocument))
                    throw new McpProtocolException($"Document '{normalizedDocument}' not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);
            }

            AnnotationPage page;
            try
            {
                page = _session.Store.GetAnnotationsPage(snapshotId, resolvedSymbol, normalizedDocument, kindFilter, limitVal, cursorObj);
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
                annotations = page.Items.Select(static a => new
                {
                    annotation_id = a.AnnotationId,
                    symbol_id = a.SymbolId,
                    kind = a.Kind,
                    value = a.Value,
                    document_path = a.DocumentPath
                }).ToList(),
                annotation_count = page.TotalCount,
                next_cursor = page.NextCursor
            };

            return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }

    [McpServerTool(Name = "lurp_retract_annotation", Title = "Lurp Retract Annotation", ReadOnly = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Retract (hard-delete) one annotation by annotation_id, scoped to the pinned snapshot — any provenance, not limited to user-authored (lurp_annotate) rows; extractor-derived rows can be removed the same way. The annotation_id is the surrogate PK from lurp_get_annotations. The delete is WHERE snapshot_id=@pinned AND annotation_id=@id — one row only, no cross-snapshot effect. Copy-forward clones allocate fresh ids, so retracting in snapshot A does not affect snapshot B's clone.")]
    public string LurpRetractAnnotation(
        long annotation_id,
        string? snapshot_id = null)
    {
        try
        {
            if (annotation_id <= 0)
                throw new McpProtocolException("annotation_id must be a positive integer.", McpErrorCode.InvalidParams);

            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            // MCP session holds a query_only connection; retraction requires a writable connection.
            // Open a short-lived writable store against the same DbPath and pin scope.
            var writable = new SqliteIndexStore(_session.DbPath);
            writable.Open();
            try
            {
                bool deleted;
                try
                {
                    deleted = writable.TryRetractAnnotation(snapshotId, annotation_id);
                }
                catch (ArgumentException ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }

                if (!deleted)
                    throw new McpProtocolException($"annotation_id {annotation_id} not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);

                var freshness = _session.GetFreshnessJson();
                var envelope = new
                {
                    status = "ok",
                    snapshot_id = snapshotId,
                    annotation_id,
                    retracted = true,
                    freshness,
                    pinned = true
                };
                return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            }
            finally
            {
                writable.Close();
            }
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }
}
