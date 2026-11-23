using System.ComponentModel;
using System.Text.Json;
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

    [McpServerTool(Name = "lurp_grep", Title = "Lurp Grep", ReadOnly = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(GrepResponse))]
    [Description("Literal/exact-text search over source content in the pinned snapshot. Returns per-occurrence results with 1-based start_line/end_line and 0-based start_column/end_column plus the full line_text. Use this instead of lurp_search when you need the exact line where a string appears. Supports pagination via cursor and case-insensitive search via ignore_case. match_count is the total match count across all pages, not the page size — use next_cursor for pagination.")]
    public string LurpGrep(
        [McpRequired] string? query = null,
        int? limit = null,
        string? cursor = null,
        bool? ignore_case = null,
        bool? include_generated = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            if (string.IsNullOrEmpty(query))
                throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);

            var limitVal = McpLimits.RequirePositiveAtMost(limit, Lurp.Handlers.GrepHandler.DefaultLimit, McpLimits.MaxLimit, "limit");

            var ignoreCase = ignore_case ?? false;
            var includeGenerated = include_generated ?? false;

            var cursorObj = McpSessionContext.DecodeCursor(cursor, TextSearchCursor.TryDecode);

            TextSearchPage page;
            try
            {
                page = store.SearchTextPage(query, snapshotId, limitVal, includeGenerated, ignoreCase, cursorObj);
            }
            catch (ArgumentException ex)
            {
                throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
            }

            var freshness = _session.GetFreshnessJson();

            var results = page.Items.Select(r => new GrepMatch(
                r.DocumentPath, r.StartLine, r.StartColumn, r.EndLine, r.EndColumn, r.LineText)).ToList();
            var envelope = new GrepResponse(snapshotId, freshness, pinned: true, query, ignoreCase,
                results, page.TotalCount, page.NextCursor);

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}

internal sealed record GrepResponse(
    string snapshot_id, object freshness, bool pinned, string query, bool ignore_case,
    IReadOnlyList<GrepMatch> results, int match_count, string? next_cursor);

internal sealed record GrepMatch(
    string document_path, int start_line, int start_column, int end_line, int end_column, string line_text);
