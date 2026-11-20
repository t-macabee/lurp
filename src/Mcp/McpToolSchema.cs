using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp;

internal static class McpToolSchema
{
    private static readonly Dictionary<string, string[]> RequiredParameters = McpServeHandler.ReadToolTypes
        .Concat(McpServeHandler.WriteToolTypes)
        .SelectMany(type => type.GetMethods())
        .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
        .Where(entry => entry.Tool is not null)
        .ToDictionary(entry => entry.Tool!.Name!, entry => entry.Method.GetParameters()
            .Where(parameter => parameter.IsDefined(typeof(McpRequiredAttribute)))
            .Select(parameter => parameter.Name!)
            .ToArray(), StringComparer.Ordinal);

    internal static McpRequestHandler<ListToolsRequestParams, ListToolsResult> ListToolsFilter(
        McpRequestHandler<ListToolsRequestParams, ListToolsResult> next) =>
        async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            foreach (var tool in result.Tools)
            {
                RequiredParameters.TryGetValue(tool.Name, out var required);
                var schema = JsonSerializer.Deserialize<JsonObject>(tool.InputSchema)!;
                if (required is { Length: > 0 })
                    schema["required"] = new JsonArray(required.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());

                DropNullArrayElements(schema);

                tool.InputSchema = JsonSerializer.SerializeToElement(schema);
            }
            return result;
        };

    // The SDK advertises string[]? items as ["string","null"]. No tool accepts a null element.
    private static void DropNullArrayElements(JsonObject schema)
    {
        if (schema["properties"] is not JsonObject properties)
            return;

        foreach (var (_, property) in properties)
        {
            if (property?["items"] is not JsonObject items || items["type"] is not JsonArray types)
                continue;

            var nonNull = types.Select(type => type?.GetValue<string>()).Where(type => type != "null").ToArray();
            if (nonNull.Length == 1)
                items["type"] = nonNull[0];
        }
    }
}
