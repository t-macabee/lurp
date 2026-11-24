using System.Text.Json.Serialization;

namespace Lurp.Workspace;

public enum ImpactDirection
{
    Downstream,
    Upstream
}

public sealed record ImpactHopSite(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("end_line")] int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

public sealed class ImpactHop
{
    public const int MaxSitesPerHop = 20;

    [JsonConstructor]
    public ImpactHop(string sourceSymbolId, string targetSymbolId, string edgeKind, string provenance, string? sourceDocument = null, int? sourceLine = null,
        int? sourceColumn = null, int? sourceEndLine = null, int? sourceEndColumn = null,
        int siteCount = 0, List<ImpactHopSite>? sites = null)
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
        SiteCount = siteCount;
        Sites = sites ?? [];
    }

    [JsonPropertyName("source_symbol_id")] public string SourceSymbolId { get; }

    [JsonPropertyName("target_symbol_id")] public string TargetSymbolId { get; }

    [JsonPropertyName("edge_kind")] public string EdgeKind { get; }

    [JsonPropertyName("provenance")] public string Provenance { get; }

    [JsonPropertyName("source_document")] public string? SourceDocument { get; }

    [JsonPropertyName("source_line")] public int? SourceLine { get; }

    [JsonPropertyName("source_column")] public int? SourceColumn { get; }

    [JsonPropertyName("source_end_line")] public int? SourceEndLine { get; }

    [JsonPropertyName("source_end_column")]
    public int? SourceEndColumn { get; }

    [JsonPropertyName("site_count")] public int SiteCount { get; internal set; }

    [JsonPropertyName("sites")] public List<ImpactHopSite> Sites { get; internal set; }
}

public sealed class ImpactPath
{
    [JsonConstructor]
    public ImpactPath(List<ImpactHop> hops, bool truncated = false, string? truncationReason = null)
    {
        Hops = hops ?? throw new ArgumentNullException(nameof(hops));
        Truncated = truncated;
        TruncationReason = truncationReason;
        TotalSteps = hops.Count;
    }

    [JsonPropertyName("hops")] public List<ImpactHop> Hops { get; }

    [JsonPropertyName("truncated")] public bool Truncated { get; }

    [JsonPropertyName("truncation_reason")]
    public string? TruncationReason { get; }

    [JsonPropertyName("total_steps")] public int TotalSteps { get; }
}