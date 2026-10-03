using ModelContextProtocol;

namespace Lurp.Mcp;

/// <summary>
///     Upper bounds for tool parameters that feed traversal or response size.
///     A caller that asks for more gets <c>InvalidParams</c> instead of a request
///     whose latency and memory are set by the client.
/// </summary>
internal static class McpLimits
{
    /// <summary>Paging cap for every <c>limit</c>/<c>tier_limit</c> parameter.</summary>
    public const int MaxLimit = 500;

    /// <summary>Cap for a capsule's <c>content_budget</c> (estimated tokens).</summary>
    public const int MaxContentBudget = 200_000;

    /// <summary>Cap for <c>max_depth</c> on <c>lurp_impact</c>.</summary>
    public const int MaxMaxDepth = 20;

    /// <summary>Cap for <c>max_hops</c> on <c>lurp_context</c>.</summary>
    public const int MaxMaxHops = 20;

    /// <summary>Cap for <c>snippet_tokens</c> on <c>lurp_search</c>.</summary>
    public const int MaxSnippetTokens = 2_000;

    /// <summary>
    ///     Rejects a value above <paramref name="max" /> with the MCP invalid-params
    ///     code. Null and below-maximum values pass through untouched.
    /// </summary>
    public static void RequireAtMost(int? value, int max, string name)
    {
        if (value.HasValue && value.Value > max)
            throw new McpProtocolException($"{name} must be <= {max}.", McpErrorCode.InvalidParams);
    }
}
