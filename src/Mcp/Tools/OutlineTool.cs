using System.ComponentModel;
using System.Text.Json;
using Lurp.Storage;
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
    [Description("List declarations in a document with 1-based line spans. Ordered by full_start. Supports pagination via limit/cursor and filtering generated declarations.")]
    public string LurpOutline(
        string? document = null,
        bool? include_generated = null,
        int? limit = null,
        string? cursor = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            var normalized = Lurp.Handlers.HandlerBootstrap.NormalizeDocumentPath(document);
            if (string.IsNullOrEmpty(normalized))
                throw new McpProtocolException("--document is required.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;
            var limitVal = limit ?? 100;
            if (limitVal < 1)
                throw new McpProtocolException("--limit must be a positive integer.", McpErrorCode.InvalidParams);

            OutlineCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = OutlineCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("--cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

            DeclarationOutlinePage? page;
            try
            {
                page = _session.Store.GetDeclarationsOutline(normalized, snapshotId, includeGenerated, limitVal, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            if (page == null)
                throw new McpProtocolException($"Document '{normalized}' not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

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
