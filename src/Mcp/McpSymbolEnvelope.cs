using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.Mcp;

internal static class McpSymbolEnvelope
{
    internal static JsonObject Build(string snapshotId, object freshness, IndexedSymbolInfo info,
        IReadOnlyList<DeclarationLocation> locations)
    {
        return new JsonObject
        {
            ["snapshot_id"] = snapshotId,
            ["freshness"] = JsonSerializer.SerializeToNode(freshness, LurpJsonOptions.Indented),
            ["pinned"] = true,
            ["symbol_id"] = info.SymbolId.Value,
            ["doc_comment_id"] = info.SymbolId.DocCommentId,
            ["assembly_identity"] = info.SymbolId.AssemblyIdentity,
            ["kind"] = info.Kind.ToString(),
            ["fully_qualified_name"] = info.FullyQualifiedName,
            ["metadata"] = JsonSerializer.SerializeToNode(SymbolMetadata.Parse(info.MetadataJson, info.SymbolId.Value), LurpJsonOptions.Indented),
            ["declaration_count"] = info.DeclarationCount,
            ["is_partial"] = info.IsPartial,
            ["locations"] = JsonSerializer.SerializeToNode(locations, LurpJsonOptions.Indented)
        };
    }
}
