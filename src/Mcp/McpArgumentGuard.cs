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
        (context, cancellationToken) =>
        {
            var arguments = context.Params?.Arguments;
            if (arguments is { Count: > 0 } && context.MatchedPrimitive is McpServerTool tool)
            {
                var schema = tool.ProtocolTool.InputSchema;
                var unknown = FindUnknownArgument(schema, arguments.Keys);
                if (unknown != null)
                    throw new McpProtocolException(
                        $"unknown parameter '{unknown}' for {tool.ProtocolTool.Name}. Valid parameters: {string.Join(", ", DeclaredNames(schema))}.",
                        McpErrorCode.InvalidParams);
            }

            return next(context, cancellationToken);
        };

    private static HashSet<string> DeclaredNames(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object && inputSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
            : [];
}
