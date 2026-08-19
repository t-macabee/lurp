using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
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
    [Description("Search source or symbols in the pinned snapshot. Type picks the store: source, symbol, or all. Uses FTS5 phrase quoting verbatim and cursor pagination for symbol search.")]
    public string LurpSearch(
        string? query = null,
        string? type = null,
        string? kind = null,
        int? limit = null,
        int? snippet_tokens = null,
        string? cursor = null,
        bool? include_generated = null,
        string? snapshot_id = null)
    {
        try
        {
            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            if (string.IsNullOrEmpty(query))
                throw new McpProtocolException("query is required.", McpErrorCode.InvalidParams);

            var typeArg = string.IsNullOrEmpty(type) ? "all" : type.ToLowerInvariant();
            if (typeArg is not ("source" or "symbol" or "all"))
                throw new McpProtocolException("type must be one of: source, symbol, all.", McpErrorCode.InvalidParams);

            var includeGenerated = include_generated ?? false;
            var limitVal = limit ?? 20;
            if (limitVal < 1)
                throw new McpProtocolException("limit must be a positive integer.", McpErrorCode.InvalidParams);

            var snippetTokens = snippet_tokens ?? 64;
            if (snippetTokens < 1)
                throw new McpProtocolException("snippet-tokens must be a positive integer.", McpErrorCode.InvalidParams);

            if (!string.IsNullOrEmpty(cursor) && typeArg != "symbol")
                throw new McpProtocolException("cursor is only supported with type=symbol.", McpErrorCode.InvalidParams);

            var freshness = _session.GetFreshnessJson();

            var results = new List<object>();
            string? nextCursor = null;

            if (typeArg is "source" or "all")
            {
                var sourceResults = _session.Store.SearchSource(query, snapshotId, limitVal, includeGenerated, snippetTokens);
                foreach (var r in sourceResults)
                {
                    results.Add(new { type = "source", document_path = r.DocumentPath, snippet = r.Snippet });
                }
            }

            if (typeArg is "symbol" or "all")
            {
                if (typeArg == "symbol")
                {
                    SearchCursor? cursorObj = null;
                    if (!string.IsNullOrEmpty(cursor))
                    {
                        cursorObj = SearchCursor.TryDecode(cursor);
                        if (cursorObj == null)
                            throw new McpProtocolException("cursor is not a valid continuation token.", McpErrorCode.InvalidParams);
                    }

                    SymbolSearchPage page;
                    try
                    {
                        page = _session.Store.SearchSymbolsPage(query, snapshotId, limitVal, includeGenerated, kind, cursorObj);
                    }
                    catch (ArgumentException ex)
                    {
                        throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                    }

                    foreach (var r in page.Items)
                    {
                        results.Add(new { type = "symbol", symbol_id = r.SymbolId, fully_qualified_name = r.FullyQualifiedName, kind = r.Kind, doc_comment_id = r.DocCommentId });
                    }

                    nextCursor = page.NextCursor;
                }
                else
                {
                    var symbolResults = _session.Store.SearchSymbols(query, snapshotId, limitVal, includeGenerated, kind);
                    foreach (var r in symbolResults)
                    {
                        results.Add(new { type = "symbol", symbol_id = r.SymbolId, fully_qualified_name = r.FullyQualifiedName, kind = r.Kind, doc_comment_id = r.DocCommentId });
                    }
                }
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
