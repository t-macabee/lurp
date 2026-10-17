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

    /// <summary>
    ///     The stored marker <c>SymbolDeclarationExtractor</c> sets for a symbol the compiler declared
    ///     (a record's <c>Equals</c>, an implicit constructor, an auto-property accessor).
    /// </summary>
    public static bool IsImplicitlyDeclared(JsonElement? metadata) =>
        IsTrue(metadata, SymbolMetadataKeys.IsImplicitlyDeclared);

    /// <summary>
    ///     <see cref="IsImplicitlyDeclared" /> as a SQL predicate over the <c>snapshot_symbols</c> row
    ///     aliased <paramref name="alias" />. A missing key reads as false.
    /// </summary>
    public static string NotImplicitlyDeclaredSql(string alias) =>
        $"COALESCE(json_extract({alias}.metadata_json, '$.{SymbolMetadataKeys.IsImplicitlyDeclared}'), 0) = 0";

    public static bool IsTrue(JsonElement? metadata, string key)
    {
        return metadata is not null
            && metadata.Value.TryGetProperty(key, out var el)
            && el.ValueKind == JsonValueKind.True;
    }
}
