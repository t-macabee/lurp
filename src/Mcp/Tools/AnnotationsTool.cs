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
    [Description("Get annotations in the pinned snapshot. Supports three modes: by symbol (resolved via docCommentId|assemblyIdentity, bare T: docCommentId, or FQN), by document (git-relative forward-slashed path), or whole-snapshot when neither is given. Optional kind filter and keyset pagination via limit/cursor (default 100, ordered by annotation_id). Annotations with document_path IS NULL (user-authored via lurp_annotate) are unreachable via document filter.")]
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
}
