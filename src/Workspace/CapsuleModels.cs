using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace
{
    internal enum ContextIntent
    {
        Inspect,
        Modify,
        Diagnose
    }

    internal sealed class CapsuleAnchor
    {
        [JsonPropertyName("symbolId")]
        public string SymbolId { get; init; }

        [JsonPropertyName("fullyQualifiedName")]
        public string FullyQualifiedName { get; init; }

        [JsonPropertyName("kind")]
        public string Kind { get; init; }

        [JsonPropertyName("source")]
        public string Source { get; init; }

        [JsonPropertyName("scope")]
        public string Scope { get; init; } = "symbol";

        [JsonPropertyName("intent")]
        public string Intent { get; init; } = "inspect";

        [JsonPropertyName("maxHops")]
        public int MaxHops { get; init; }

        [JsonPropertyName("snapshotId")]
        public string SnapshotId { get; init; } = "";

        [JsonPropertyName("affectedProjects")]
        public List<string> AffectedProjects { get; init; } = [];

        [JsonPropertyName("changeObjective")]
        public string? ChangeObjective { get; init; }

        [JsonPropertyName("provenance")]
        public string Provenance { get; init; } = "compiler_proved";

        [JsonPropertyName("extractorIdentity")]
        public string ExtractorIdentity { get; init; } = "";

        [JsonPropertyName("locations")]
        public List<DeclarationLocation> Locations { get; init; } = [];

        public CapsuleAnchor(string symbolId, string fullyQualifiedName, string kind, string source)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }
    }

    internal sealed class CapsuleItem
    {
        [JsonPropertyName("symbolId")]
        public string SymbolId { get; init; }

        [JsonPropertyName("kind")]
        public string Kind { get; init; }

        [JsonPropertyName("fullyQualifiedName")]
        public string FullyQualifiedName { get; init; }

        [JsonPropertyName("provenance")]
        public string Provenance { get; init; }

        [JsonPropertyName("edgeKind")]
        public string EdgeKind { get; init; }

        [JsonPropertyName("source")]
        public string? Source { get; init; }

        [JsonPropertyName("documentPath")]
        public string? DocumentPath { get; init; }

        [JsonPropertyName("startLine")]
        public int? StartLine { get; init; }

        [JsonPropertyName("startColumn")]
        public int? StartColumn { get; init; }

        [JsonPropertyName("endLine")]
        public int? EndLine { get; init; }

        [JsonPropertyName("endColumn")]
        public int? EndColumn { get; init; }

        [JsonPropertyName("inclusionReason")]
        public string? InclusionReason { get; init; }

        public CapsuleItem(string symbolId,string kind,string fullyQualifiedName,string provenance,string edgeKind,string? source = null,
            DeclarationLocation? location = null, string? inclusionReason = null)
        {
            SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            FullyQualifiedName = fullyQualifiedName ?? throw new ArgumentNullException(nameof(fullyQualifiedName));
            Provenance = provenance ?? string.Empty;
            EdgeKind = edgeKind ?? throw new ArgumentNullException(nameof(edgeKind));
            Source = source;
            DocumentPath = location?.DocumentPath;
            StartLine = location?.StartLine;
            StartColumn = location?.StartColumn;
            EndLine = location?.EndLine;
            EndColumn = location?.EndColumn;
            InclusionReason = inclusionReason;
        }
    }

    internal sealed record CapsuleConstraint(
        [property: JsonPropertyName("value")] string Value,
        [property: JsonPropertyName("origin")] string Origin,
        [property: JsonPropertyName("annotationKind")] string? AnnotationKind = null,
        [property: JsonPropertyName("symbolId")] string? SymbolId = null);

    internal sealed record LikelyChangeSite(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("rank")] int Rank,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("symbolId")] string SymbolId);

    internal sealed record CapsuleTopology(
        [property: JsonPropertyName("current")] List<ImpactPath> Current,
        [property: JsonPropertyName("target")] List<ImpactPath> Target,
        [property: JsonPropertyName("annotations")] List<CapsuleConstraint> Annotations);

    internal sealed class UncertaintyEntry
    {
        [JsonPropertyName("symbolIds")]
        public List<string> SymbolIds { get; init; }

        [JsonPropertyName("relationshipKind")]
        public string RelationshipKind { get; init; }

        [JsonPropertyName("description")]
        public string Description { get; init; }

        public UncertaintyEntry(List<string> symbolIds, string relationshipKind, string description)
        {
            SymbolIds = symbolIds ?? throw new ArgumentNullException(nameof(symbolIds));
            RelationshipKind = relationshipKind ?? throw new ArgumentNullException(nameof(relationshipKind));
            Description = description ?? throw new ArgumentNullException(nameof(description));
        }
    }

    internal sealed class VerificationSuggestion
    {
        [JsonPropertyName("testId")]
        public string TestId { get; init; }

        [JsonPropertyName("testName")]
        public string TestName { get; init; }

        [JsonPropertyName("description")]
        public string Description { get; init; }

        [JsonPropertyName("command")]
        public string? Command { get; init; }

        [JsonPropertyName("reason")]
        public string Reason { get; init; }

        public VerificationSuggestion(string testId, string testName, string description, string? command = null, string reason = "direct_test_coverage")
        {
            TestId = testId ?? throw new ArgumentNullException(nameof(testId));
            TestName = testName ?? throw new ArgumentNullException(nameof(testName));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Command = command;
            Reason = reason ?? throw new ArgumentNullException(nameof(reason));
        }
    }

    internal sealed record TruncationEntry(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("reason")] string Reason);

    internal sealed class ContextCapsule
    {
        [JsonPropertyName("anchor")]
        public CapsuleAnchor Anchor { get; init; }

        [JsonPropertyName("contracts")]
        public List<CapsuleItem> Contracts { get; init; } = [];

        [JsonPropertyName("directCallees")]
        public List<CapsuleItem> DirectCallees { get; init; } = [];

        [JsonPropertyName("directCallers")]
        public List<CapsuleItem> DirectCallers { get; init; } = [];

        [JsonPropertyName("registeredImplementations")]
        public List<CapsuleItem> RegisteredImplementations { get; init; } = [];

        [JsonPropertyName("relevantTests")]
        public List<CapsuleItem> RelevantTests { get; init; } = [];

        [JsonPropertyName("secondDegreeContext")]
        public List<CapsuleItem> SecondDegreeContext { get; init; } = [];

        [JsonPropertyName("surroundingSource")]
        public List<CapsuleItem> SurroundingSource { get; init; } = [];

        [JsonPropertyName("incomingPaths")]
        public List<ImpactPath> IncomingPaths { get; init; } = [];

        [JsonPropertyName("outgoingPaths")]
        public List<ImpactPath> OutgoingPaths { get; init; } = [];

        [JsonPropertyName("constraints")]
        public List<CapsuleConstraint> Constraints { get; init; } = [];

        [JsonPropertyName("inclusionReasons")]
        public Dictionary<string, string> InclusionReasons { get; init; } = [];

        [JsonPropertyName("likelyChangeSites")]
        public List<LikelyChangeSite> LikelyChangeSites { get; init; } = [];

        [JsonPropertyName("affectedPublicSurfaces")]
        public List<CapsuleItem> AffectedPublicSurfaces { get; init; } = [];

        [JsonPropertyName("topology")]
        public CapsuleTopology Topology { get; set; } = new([], [], []);

        [JsonPropertyName("completeness")]
        public SnapshotCompleteness? Completeness { get; set; }

        [JsonPropertyName("budget")]
        public int Budget { get; init; }

        [JsonPropertyName("estimatedTokens")]
        public int EstimatedTokens { get; set; }

        [JsonPropertyName("truncated")]
        public bool Truncated { get; set; }

        [JsonPropertyName("truncatedCategories")]
        public List<string> TruncatedCategories { get; set; } = [];

        [JsonPropertyName("omittedTiers")]
        public List<TruncationEntry> OmittedTiers { get; set; } = [];

        [JsonPropertyName("uncertainties")]
        public List<UncertaintyEntry> Uncertainties { get; init; } = [];

        [JsonPropertyName("suggestedVerification")]
        public List<VerificationSuggestion> SuggestedVerification { get; init; } = [];

        public ContextCapsule(CapsuleAnchor anchor)
        {
            Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        }
    }
}
