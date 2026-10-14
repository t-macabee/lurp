using System.ComponentModel;
using Lurp.Storage;
using ModelContextProtocol;

namespace Lurp.Mcp;

/// <summary>
///     Upper bounds for tool parameters that feed traversal or response size.
///     A caller that asks for more gets <c>InvalidParams</c> instead of a request
///     whose latency and memory are set by the client.
/// </summary>
internal static class McpLimits
{
    [Description("`limit` and `tier_limit` on every tool except `lurp_dead_candidates`; `max_documents`, `max_mismatches` and the length of `documents` on `lurp_status`")]
    public const int MaxLimit = 500;

    [Description("`limit` on `lurp_dead_candidates`")]
    public const int MaxDeadCandidatesLimit = DeadCandidateLiveness.MaxPageSize;

    [Description("`content_budget` on `lurp_context` (estimated tokens)")]
    public const int MaxContentBudget = 200_000;

    [Description("`max_depth` on `lurp_impact`")]
    public const int MaxMaxDepth = 20;

    [Description("`max_hops` on `lurp_context`")]
    public const int MaxMaxHops = 20;

    [Description("`snippet_tokens` on `lurp_search`")]
    public const int MaxSnippetTokens = 2_000;

    /// <remarks>
    ///     MCP clients cut larger results without counting them (Claude Code: 4 × its
    ///     25,000-token limit, in characters).
    /// </remarks>
    [Description("`max_bytes` on `lurp_status` (UTF-8 bytes)")]
    public const int MaxStatusBytes = 400_000;

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
