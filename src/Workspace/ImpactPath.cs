using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace
{
    public enum ImpactDirection
    {
        Downstream,
        Upstream
    }

    public sealed class ImpactHop
    {
        [JsonPropertyName("source_symbol_id")]
        public string SourceSymbolId { get; }
        [JsonPropertyName("target_symbol_id")]
        public string TargetSymbolId { get; }
        [JsonPropertyName("edge_kind")]
        public string EdgeKind { get; }
        [JsonPropertyName("provenance")]
        public string Provenance { get; }
        [JsonPropertyName("source_document")]
        public string? SourceDocument { get; }
        [JsonPropertyName("source_line")]
        public int? SourceLine { get; }
        [JsonPropertyName("source_column")]
        public int? SourceColumn { get; }
        [JsonPropertyName("source_end_line")]
        public int? SourceEndLine { get; }
        [JsonPropertyName("source_end_column")]
        public int? SourceEndColumn { get; }

        [JsonConstructor]
        public ImpactHop(string sourceSymbolId,string targetSymbolId,string edgeKind,string provenance,string? sourceDocument = null,int? sourceLine = null,
            int? sourceColumn = null, int? sourceEndLine = null, int? sourceEndColumn = null)
        {
            SourceSymbolId = sourceSymbolId ?? throw new ArgumentNullException(nameof(sourceSymbolId));
            TargetSymbolId = targetSymbolId ?? throw new ArgumentNullException(nameof(targetSymbolId));
            EdgeKind = edgeKind ?? throw new ArgumentNullException(nameof(edgeKind));
            Provenance = provenance ?? string.Empty;
            SourceDocument = sourceDocument;
            SourceLine = sourceLine;
            SourceColumn = sourceColumn;
            SourceEndLine = sourceEndLine;
            SourceEndColumn = sourceEndColumn;
        }
    }

    public sealed class ImpactPath
    {
        [JsonPropertyName("hops")]
        public List<ImpactHop> Hops { get; }
        [JsonPropertyName("truncated")]
        public bool Truncated { get; }
        [JsonPropertyName("truncation_reason")]
        public string? TruncationReason { get; }
        [JsonPropertyName("total_steps")]
        public int TotalSteps { get; }
        [JsonPropertyName("semantic_causes")]
        public List<SemanticChange> SemanticCauses { get; }

        [JsonConstructor]
        public ImpactPath(List<ImpactHop> hops,bool truncated = false,string? truncationReason = null,List<SemanticChange>? semanticCauses = null)
        {
            Hops = hops ?? throw new ArgumentNullException(nameof(hops));
            Truncated = truncated;
            TruncationReason = truncationReason;
            TotalSteps = hops.Count;
            SemanticCauses = semanticCauses ?? [];
        }
    }
}
