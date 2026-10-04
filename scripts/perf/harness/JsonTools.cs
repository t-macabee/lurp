using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal static class JsonTools
{
    public static string? GetString(JsonObject obj, string key)
        => obj[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public static JsonObject? TryParseObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
            return null;

        try
        {
            return JsonNode.Parse(text[start..]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
