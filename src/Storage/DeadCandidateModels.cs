using System.Text.Json.Serialization;

namespace Lurp.Storage;

public static class DeadCandidateReason
{
    public const string NoIncomingLiveEdges = "no_incoming_live_edges";
    public const string BindingIncompleteness = "binding_incompleteness";
    public const string PublicSurface = "public_surface";
    public const string EfConvention = "ef_convention";
    public const string SerializationConvention = "serialization_convention";
    public const string FrameworkConvention = "framework_convention";
    public const string PossibleDispatch = "possible_dispatch";
    public const string NameCandidate = "name_candidate";
    public const string RuntimeUnknown = "runtime_unknown";
    public const string GeneratedExcluded = "generated_excluded";
    public const string TestHarness = "test_harness";
    public const string EntryPointConvention = "entry_point_convention";
}

public static class DeadCandidateStatus
{
    public const string ProvedDead = "proved_dead";
    public const string UncertainDead = "uncertain_dead";
    public const string Unresolved = "unresolved";
    public const string Uncertain = "uncertain";
}

public sealed class DeadCandidateEntry
{
    public DeadCandidateEntry(
        string symbolId,
        string? fqn,
        string kind,
        string? accessibility,
        string? documentPath,
        List<DeclarationLocation> locations,
        string? projectName,
        int declarationCount,
        bool isGenerated,
        string status,
        string reason,
        List<DeadCandidateUncertainty> uncertainties,
        DeadCandidateIncomingSummary incomingEdgeSummary)
    {
        SymbolId = symbolId;
        Fqn = fqn;
        Kind = kind;
        Accessibility = accessibility;
        DocumentPath = documentPath;
        Locations = locations;
        ProjectName = projectName;
        DeclarationCount = declarationCount;
        IsGenerated = isGenerated;
        Status = status;
        Reason = reason;
        Uncertainties = uncertainties;
        IncomingEdgeSummary = incomingEdgeSummary;
    }

    public string SymbolId { get; }
    public string? Fqn { get; }
    public string Kind { get; }
    public string? Accessibility { get; }
    public string? DocumentPath { get; }
    public List<DeclarationLocation> Locations { get; }
    public string? ProjectName { get; }
    public int DeclarationCount { get; }
    public bool IsGenerated { get; }
    public string Status { get; }
    public string Reason { get; }
    public List<DeadCandidateUncertainty> Uncertainties { get; }
    public DeadCandidateIncomingSummary IncomingEdgeSummary { get; }
}

public sealed class DeadCandidateUncertainty
{
    public DeadCandidateUncertainty(List<string> symbolIds, string relationshipKind, string description, string? boundaryId = null)
    {
        SymbolIds = symbolIds;
        RelationshipKind = relationshipKind;
        Description = description;
        BoundaryId = boundaryId;
    }

    [JsonPropertyName("symbol_ids")]
    public List<string> SymbolIds { get; }

    [JsonPropertyName("relationship_kind")]
    public string RelationshipKind { get; }

    [JsonPropertyName("description")]
    public string Description { get; }

    [JsonPropertyName("boundary_id")]
    public string? BoundaryId { get; }
}

public sealed class DeadCandidateIncomingSummary
{
    public DeadCandidateIncomingSummary(int liveStrong, int liveWeak, Dictionary<string,int> provenanceBreakdown, Dictionary<string,int> kindBreakdown)
    {
        LiveStrong = liveStrong;
        LiveWeak = liveWeak;
        ProvenanceBreakdown = provenanceBreakdown;
        KindBreakdown = kindBreakdown;
    }

    [JsonPropertyName("live_strong")]
    public int LiveStrong { get; }

    [JsonPropertyName("live_weak")]
    public int LiveWeak { get; }

    [JsonPropertyName("provenance_breakdown")]
    public Dictionary<string,int> ProvenanceBreakdown { get; }

    [JsonPropertyName("kind_breakdown")]
    public Dictionary<string,int> KindBreakdown { get; }
}
