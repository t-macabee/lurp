using System.Text.Json;

namespace Lurp.Storage;

public static class SymbolMetadata
{
    public static JsonElement? Parse(string? metadataJson, string symbolId)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse metadata JSON for symbol '{symbolId}'.", ex);
        }
    }
}
