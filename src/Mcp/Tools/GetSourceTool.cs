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

    [McpServerTool(Name = "lurp_get_source", Title = "Lurp Get Source", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Fetch source text for a document in the pinned snapshot. Supports line windowing: start_line/end_line are 1-based inclusive; context_lines expands symmetrically. When no window is given the whole file is returned.")]
    public string LurpGetSource(
        string? document = null,
        int? start_line = null,
        int? end_line = null,
        int? context_lines = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            var normalized = HandlerBootstrap.NormalizeDocumentPath(document);
            if (string.IsNullOrEmpty(normalized))
                throw new McpProtocolException("--document is required.", McpErrorCode.InvalidParams);

            if (start_line.HasValue && start_line.Value < 1)
                throw new McpProtocolException("--start-line must be a positive integer (>=1).", McpErrorCode.InvalidParams);
            if (end_line.HasValue && end_line.Value < 1)
                throw new McpProtocolException("--end-line must be a positive integer (>=1).", McpErrorCode.InvalidParams);
            if (context_lines.HasValue && context_lines.Value < 0)
                throw new McpProtocolException("--context-lines must be a non-negative integer.", McpErrorCode.InvalidParams);
            if (context_lines.HasValue && start_line == null && end_line == null)
                throw new McpProtocolException("--context-lines requires --start-line or --end-line.", McpErrorCode.InvalidParams);
            if (start_line.HasValue && end_line.HasValue && start_line.Value > end_line.Value)
                throw new McpProtocolException("--start-line must be <= --end-line.", McpErrorCode.InvalidParams);

            SourceSlice? slice;
            try
            {
                slice = _session.Store.GetSourceSlice(normalized, snapshotId, start_line, end_line, context_lines);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            if (slice == null)
                throw new McpProtocolException($"Document '{normalized}' not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                source = slice.Source,
                document = normalized,
                start_line = slice.StartLine,
                end_line = slice.EndLine,
                total_lines = slice.TotalLines,
                truncated = slice.Truncated
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
