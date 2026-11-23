using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Handlers;

namespace Lurp.Mcp;

internal static class McpSymbolEnvelope
{
    internal static McpSymbolResponse Build(string snapshotId, object freshness, IndexedSymbolInfo info,
        IReadOnlyList<DeclarationLocation> locations) =>
        new(snapshotId, freshness, pinned: true, info.SymbolId.Value, info.SymbolId.DocCommentId,
            info.SymbolId.AssemblyIdentity, info.Kind.ToString(), info.FullyQualifiedName,
            SymbolMetadata.Parse(info.MetadataJson, info.SymbolId.Value), info.DeclarationCount, info.IsPartial, locations);
}

internal record McpSymbolResponse(
    string snapshot_id, object freshness, bool pinned, string symbol_id, string doc_comment_id,
    string assembly_identity, string kind, string? fully_qualified_name, JsonElement? metadata,
    int declaration_count, bool is_partial, IReadOnlyList<DeclarationLocation> locations);

internal sealed record McpGetSymbolResponse : McpSymbolResponse
{
    internal McpGetSymbolResponse(McpSymbolResponse symbol, string? source,
        IReadOnlyList<SurfacePayloads.AnnotationPayload> annotations) : base(symbol)
    {
        this.source = source;
        this.annotations = annotations;
    }

    [JsonPropertyOrder(1)]
    public string? source { get; }

    [JsonPropertyOrder(1)]
    public IReadOnlyList<SurfacePayloads.AnnotationPayload> annotations { get; }
}
