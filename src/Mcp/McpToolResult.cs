using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp;

internal static class McpToolResult
{
    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            // The tool has already serialized the payload for the text block.
            if (result.IsError != true && result.Content is [TextContentBlock text])
                result.StructuredContent = JsonSerializer.Deserialize<JsonElement>(text.Text);
            return result;
        };
}
