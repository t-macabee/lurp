using System.ComponentModel;
using System.Text.Json;
using Lurp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class GrepTool
{
    private readonly McpSessionContext _session;

    public GrepTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_grep", Title = "Lurp Grep", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Literal/exact-text search over source content in the pinned snapshot. Returns per-occurrence results with 1-based start_line/end_line and 0-based start_column/end_column plus the full line_text. Use this instead of lurp_search when you need the exact line where a string appears. Supports pagination via cursor and case-insensitive search via ignore_case. match_count is the total match count across all pages, not the page size — use next_cursor for pagination.")]
    public string LurpGrep(
        string? query = null,
        int? limit = null,
        string? cursor = null,
        bool? ignore_case = null,
        bool? include_generated = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            if (string.IsNullOrEmpty(query))
                throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);

            var limitVal = limit ?? 50;
            if (limitVal < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);

            var ignoreCase = ignore_case ?? false;
            var includeGenerated = include_generated ?? false;

            TextSearchCursor? cursorObj = null;
            if (!string.IsNullOrEmpty(cursor))
            {
                cursorObj = TextSearchCursor.TryDecode(cursor);
                if (cursorObj == null)
                    throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
            }

            TextSearchPage page;
            try
            {
                page = _session.Store.SearchTextPage(query, snapshotId, limitVal, includeGenerated, ignoreCase, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();

            var results = new List<object>();
            foreach (var r in page.Items)
            {
                results.Add(new
                {
                    document_path = r.DocumentPath,
                    start_line = r.StartLine,
                    start_column = r.StartColumn,
                    end_line = r.EndLine,
                    end_column = r.EndColumn,
                    line_text = r.LineText
                });
            }

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                query,
                ignore_case = ignoreCase,
                results,
                match_count = page.TotalCount,
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
