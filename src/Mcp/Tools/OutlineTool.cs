using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class OutlineTool
{
    private readonly McpSessionContext _session;

    public OutlineTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_outline", Title = "Lurp Outline", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List declarations in a document with 1-based line spans. Ordered by full_start. Supports pagination via limit/cursor and filtering generated declarations. declaration_count is the total match count across all pages, not the page size — use next_cursor for pagination. Compiler-generated members (record Equals, ToString and similar) are not listed.")]
    public string LurpOutline(
        [McpRequired] string? document = null,
        bool? include_generated = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            var normalized = Lurp.Handlers.HandlerBootstrap.NormalizeDocumentPath(document);
            if (string.IsNullOrEmpty(normalized))
                throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;
            var limitVal = McpLimits.RequirePositiveAtMost(limit, OutlineHandler.DefaultLimit, McpLimits.MaxLimit, "limit");

            var cursorObj = McpSessionContext.DecodeCursor(cursor, OutlineCursor.TryDecode);

            DeclarationOutlinePage? page;
            try
            {
                page = store.GetDeclarationsOutline(normalized, snapshotId, includeGenerated, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            if (page == null)
                throw new McpProtocolException(DocumentMessages.NotFound(normalized, snapshotId), McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

            var declarations = page.Items.Select(SurfacePayloads.OutlineDeclaration).ToList();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                document = normalized,
                declarations,
                declaration_count = page.TotalCount,
                next_cursor = page.NextCursor
            };

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}
