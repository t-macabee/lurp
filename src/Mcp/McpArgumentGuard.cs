using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp;

/// <summary>
///     Rejects a tool argument whose name the tool's input schema does not declare,
///     as the CLI rejects an unknown flag. Without it a typo (<c>max_depht</c>) runs
///     the tool with the default and answers a different question.
/// </summary>
internal static class McpArgumentGuard
{
    internal static string? FindUnknownArgument(JsonElement inputSchema, IEnumerable<string> argumentNames)
    {
        var declared = DeclaredNames(inputSchema);
        return argumentNames.FirstOrDefault(name => !declared.Contains(name));
    }

    internal static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            if (context.MatchedPrimitive is not McpServerTool tool)
                return await next(context, cancellationToken);

            var arguments = context.Params?.Arguments;
            if (arguments is { Count: > 0 })
            {
                var schema = tool.ProtocolTool.InputSchema;
                var unknown = FindUnknownArgument(schema, arguments.Keys);
                if (unknown != null)
                    return McpErrorMapper.ToToolResult(new McpProtocolException(
                        $"unknown parameter '{unknown}' for {tool.ProtocolTool.Name}. Valid parameters: {string.Join(", ", DeclaredNames(schema))}.",
                        McpErrorCode.InvalidParams));
            }

            try
            {
                return await next(context, cancellationToken);
            }
            catch (McpProtocolException ex)
            {
                return McpErrorMapper.ToToolResult(ex);
            }
        };

    private static HashSet<string> DeclaredNames(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object && inputSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
}
