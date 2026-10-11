using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace;

/// <summary>
///     One semantic cause as lurp_impact and the capsule's top-level <c>semantic_causes</c> write it (B60).
///     Both surfaces use this item, so their shape cannot drift apart.
/// </summary>
internal sealed record SemanticCauseJson(
    [property: JsonPropertyName("from_snapshot_id")] string FromSnapshotId,
    [property: JsonPropertyName("to_snapshot_id")] string ToSnapshotId,
    [property: JsonPropertyName("change_type")] string ChangeType,
    [property: JsonPropertyName("symbol_id")] string SymbolId,
    [property: JsonPropertyName("detail")] JsonElement? Detail)
{
    public static SemanticCauseJson From(SemanticChange change)
    {
        return new SemanticCauseJson(
            change.FromSnapshotId,
            change.ToSnapshotId,
            change.ChangeType,
            change.SymbolId,
            change.DetailJson != null ? JsonSerializer.Deserialize<JsonElement>(change.DetailJson) : null);
    }
}
