using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class GetSourceTool
{
    private readonly McpSessionContext _session;

    public GetSourceTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_get_source", Title = "Lurp Get Source", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(GetSourceResponse))]
    [Description("Fetch source text for a document in the pinned snapshot. Supports line windowing: start_line/end_line are 1-based inclusive; context_lines expands symmetrically. When no window is given the whole file is returned. Pass outline=true to include the declaration outline in the same response; compiler-generated members (record Equals, ToString and similar) are not listed.")]
    public string LurpGetSource(
        [McpRequired] string? document = null,
        int? start_line = null,
        int? end_line = null,
        int? context_lines = null,
        string? snapshot_id = null,
        bool? outline = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            var normalized = HandlerBootstrap.NormalizeDocumentPath(document);
            if (string.IsNullOrEmpty(normalized))
                throw new McpProtocolException("document is required.", McpErrorCode.InvalidParams);

            if (start_line.HasValue && start_line.Value < 1)
                throw new McpProtocolException("start_line must be a positive integer (>=1).", McpErrorCode.InvalidParams);
            if (end_line.HasValue && end_line.Value < 1)
                throw new McpProtocolException("end_line must be a positive integer (>=1).", McpErrorCode.InvalidParams);
            if (context_lines.HasValue && context_lines.Value < 0)
                throw new McpProtocolException("context_lines must be a non-negative integer.", McpErrorCode.InvalidParams);
            if (context_lines.HasValue && start_line == null && end_line == null)
                throw new McpProtocolException("context_lines requires start_line or end_line.", McpErrorCode.InvalidParams);
            if (start_line.HasValue && end_line.HasValue && start_line.Value > end_line.Value)
                throw new McpProtocolException("start_line must be <= end_line.", McpErrorCode.InvalidParams);

            SourceSlice? slice;
            try
            {
                slice = store.GetSourceSlice(normalized, snapshotId, start_line, end_line, context_lines);
            }
            catch (SourceLineOutOfRangeException ex)
            {
                throw new McpProtocolException(
                    $"start_line {ex.RequestedStartLine} is beyond the last line ({ex.TotalLines}) of '{normalized}'.",
                    McpErrorCode.InvalidParams);
            }

            if (slice == null)
                throw new McpProtocolException(DocumentMessages.NotFound(normalized, snapshotId), McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

            IReadOnlyList<SurfacePayloads.OutlineDeclarationPayload>? outlinePayload = null;
            string? outlineNextCursor = null;
            int? outlineCount = null;
            if (outline == true)
            {
                try
                {
                    var outlinePage = store.GetDeclarationsOutline(normalized, snapshotId, false, OutlineHandler.DefaultLimit, null);
                    if (outlinePage != null)
                    {
                        outlinePayload = outlinePage.Items.Select(SurfacePayloads.OutlineDeclaration).ToList();
                        outlineNextCursor = outlinePage.NextCursor;
                        outlineCount = outlinePage.TotalCount;
                    }
                }
                catch (ArgumentException ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }
            }

            var envelope = new GetSourceResponse(snapshotId, freshness, pinned: true,
                slice.Source, normalized, slice.StartLine, slice.EndLine, slice.TotalLines, slice.Truncated,
                outlinePayload, outlineNextCursor, outlineCount);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}

internal sealed record GetSourceResponse(
    string snapshot_id, object freshness, bool pinned, string source, string document,
    int start_line, int end_line, int total_lines, bool truncated,
    IReadOnlyList<SurfacePayloads.OutlineDeclarationPayload>? outline,
    string? outline_next_cursor, int? outline_declaration_count);
