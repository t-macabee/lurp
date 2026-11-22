using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class SearchTool
{
    private readonly McpSessionContext _session;

    public SearchTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_search", Title = "Lurp Search", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Search source or symbols in the pinned snapshot. Type picks the store: source, symbol, or all. Uses FTS5 phrase quoting verbatim and cursor pagination for symbol search. Source snippets keep the whole first match, even if it exceeds snippet_tokens.")]
    public string LurpSearch(
        [McpRequired] string? query = null,
        string? type = null,
        string? kind = null,
        int? limit = null,
        int? snippet_tokens = null,
        string? cursor = null,
        bool? include_generated = null,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default) =>
        _session.RunReadTool(snapshot_id, (store, snapshotId) =>
        {
            if (string.IsNullOrEmpty(query))
                throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);

            var typeArg = string.IsNullOrEmpty(type) ? "all" : type.ToLowerInvariant();
            if (typeArg is not ("source" or "symbol" or "all"))
                throw new McpProtocolException("type must be one of: source, symbol, all.", McpErrorCode.InvalidParams);

            string? canonicalKind = null;
            if (!string.IsNullOrEmpty(kind) && !DeadCandidateLiveness.TryGetCandidateKind(kind, out canonicalKind))
                throw new McpProtocolException($"kind must be one of: {DeadCandidateLiveness.CandidateKindsText}. Got '{kind}'.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;
            var limitVal = McpLimits.RequirePositiveAtMost(limit, SearchHandler.DefaultLimit, McpLimits.MaxLimit, "limit");

            var snippetTokens = McpLimits.RequirePositiveAtMost(snippet_tokens, SearchHandler.DefaultSnippetTokens, McpLimits.MaxSnippetTokens, "snippet_tokens");

            if (!string.IsNullOrEmpty(cursor) && typeArg != "symbol")
                throw new McpProtocolException("cursor is only supported with type=symbol.", McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

            var results = new List<object>();
            string? nextCursor = null;

            if (typeArg is "source" or "all")
            {
                var sourceResults = store.SearchSource(query, snapshotId, limitVal, includeGenerated, snippetTokens);
                foreach (var r in sourceResults)
                {
                    results.Add(new { type = "source", document_path = r.DocumentPath, snippet = r.Snippet });
                }
            }

            if (typeArg is "symbol" or "all")
            {
                var cursorObj = McpSessionContext.DecodeCursor(cursor, SearchCursor.TryDecode);

                SymbolSearchPage page;
                try
                {
                    page = store.SearchSymbolsPage(query, snapshotId, limitVal, includeGenerated, canonicalKind, cursorObj);
                }
                catch (ArgumentException ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }

                foreach (var r in page.Items)
                {
                    results.Add(new { type = "symbol", symbol_id = r.SymbolId, fully_qualified_name = r.FullyQualifiedName, kind = r.Kind, doc_comment_id = r.DocCommentId });
                }

                if (typeArg == "symbol")
                    nextCursor = page.NextCursor;
            }

            var envelope = new
            {
                snapshot_id = snapshotId,
                freshness,
                pinned = true,
                query,
                type = typeArg,
                results,
                next_cursor = nextCursor
            };

            return JsonSerializer.Serialize(envelope, LurpJsonOptions.Indented);
        }, cancellationToken);
}
