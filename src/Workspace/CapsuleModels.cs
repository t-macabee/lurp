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

    /// <summary>
    /// Canonical relationship vocabulary for capsule items. The relationship
    /// states how the item relates to the anchor, independently of the graph
    /// edge kind that surfaced it: an item built from a Calls hop can still be
    /// an indirect dispatch candidate rather than a direct caller/callee.
    /// </summary>
    internal static class CapsuleRelationship
    {
        /// <summary>A source-level call that directly targets the anchor.</summary>
        public const string DirectCaller = "direct_caller";

        /// <summary>A source-level call or construction directly targeted by the anchor.</summary>
        public const string DirectCallee = "direct_callee";

        /// <summary>
        /// A symbol connected to the anchor through Calls + MayDispatchTo, in
        /// either direction. As a caller: the caller directly calls an
        /// interface/abstract member (compiler-proved Calls edge); that member
        /// may dispatch to the anchor at runtime (MayDispatchTo edge). As a
        /// callee: the anchor calls an interface/abstract member that may
        /// dispatch to this implementation at runtime. In both directions the
        /// composed claim is possible : the runtime dispatch target is not
        /// compiler-established.
        /// </summary>
        public const string IndirectDispatchCandidate = "indirect_dispatch_candidate";
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

        /// <summary>
        /// How the item relates to the anchor when the graph edge kind alone
        /// cannot say (see <see cref="CapsuleRelationship"/>). Null when the
        /// edge kind already conveys the relationship.
        /// </summary>
        [JsonPropertyName("relationship")]
        public string? Relationship { get; init; }

        /// <summary>
        /// Whether the item is a direct source-level relationship to the
        /// anchor. False for dispatch-mediated items; they must never be read
        /// as direct callers. Null when directness is not part of the claim.
        /// </summary>
        [JsonPropertyName("direct")]
        public bool? Direct { get; init; }

        // Capsule items are ordinarily authored through the invariant-enforcing
        // constructor below, but emitted capsules are also machine-readable
        // artifacts. A parameterless JSON constructor lets the JSON contract
        // round-trip the flattened location fields without inventing a
        // synthetic `location` property solely for deserialization.
        [JsonConstructor]
        public CapsuleItem()
        {
            SymbolId = string.Empty;
            Kind = string.Empty;
            FullyQualifiedName = string.Empty;
            Provenance = string.Empty;
            EdgeKind = string.Empty;
        }

        public CapsuleItem(string symbolId,string kind,string fullyQualifiedName,string provenance,string edgeKind,string? source = null,
            DeclarationLocation? location = null, string? inclusionReason = null, string? relationship = null, bool? direct = null)
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
            Relationship = relationship;
            Direct = direct;
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
        [property: JsonPropertyName("current")] CapsuleTopologyReference Current,
        [property: JsonPropertyName("target")] List<ImpactPath> Target,
        [property: JsonPropertyName("annotations")] List<CapsuleConstraint> Annotations);

    // The capsule's current topology is the union of incomingPaths and
    // outgoingPaths. Those collections are serialized once, in their own
    // sections; this reference summary preserves the topology meaning without
    // duplicating the path data.
    internal sealed record CapsuleTopologyReference(
        [property: JsonPropertyName("incomingReference")] string IncomingReference,
        [property: JsonPropertyName("outgoingReference")] string OutgoingReference,
        [property: JsonPropertyName("incomingPathCount")] int IncomingPathCount,
        [property: JsonPropertyName("outgoingPathCount")] int OutgoingPathCount,
        [property: JsonPropertyName("totalHopCount")] int TotalHopCount)
    {
        public static CapsuleTopologyReference Empty { get; } = new("", "", 0, 0, 0);
    }

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

    /// <summary>
    /// One capsule tier, fetched on its own and paged. Carries anchor identity only :
    /// no anchor source, no other tiers, no paths : because the caller already has the
    /// capsule this continues and is asking for the one section it could not fit.
    /// </summary>
    internal sealed record CapsuleTierPage(
        string TierName,
        string SymbolId,
        string FullyQualifiedName,
        string Kind,
        int TotalItems,
        int Offset,
        List<CapsuleItem> Items,
        bool HasMore);

    /// <summary>
    /// Advisory restatement of the capsule's two token figures under role-named
    /// fields. <c>budgetBasis</c> is what <c>--budget</c> bounded (the content
    /// measure; per-item identity/provenance framing is excluded navigation
    /// metadata). <c>delivery</c> is the whole emitted file the consumer must
    /// reserve a context window for. The two are not interchangeable and
    /// <c>delivery</c> is always the larger; <c>windowSizingField</c> names the
    /// one to size from.
    /// </summary>
    internal sealed record TokenEstimateAdvisory(
        [property: JsonPropertyName("budgetBasis")] int BudgetBasis,
        [property: JsonPropertyName("delivery")] int Delivery)
    {
        [JsonPropertyName("basis")]
        public string Basis => "content; per-item identity/provenance framing excluded";

        [JsonPropertyName("windowSizingField")]
        public string WindowSizingField => "delivery";
    }

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

        // Null once the budget enforcer drops it: zeroed counts would read as a
        // positive "no references" claim. Its absence is declared in omittedTiers.
        [JsonPropertyName("topology")]
        public CapsuleTopology? Topology { get; set; } = new(CapsuleTopologyReference.Empty, [], []);

        [JsonPropertyName("completeness")]
        public SnapshotCompleteness? Completeness { get; set; }

        [JsonPropertyName("budget")]
        public int Budget { get; init; }

        /// <summary>
        /// The settled CONTENT measure this capsule was budgeted against: anchor
        /// and item source plus the serialized weight of the substantive
        /// non-source sections. Per-item identity and provenance framing is
        /// navigation metadata and is deliberately not counted, so the emitted
        /// file is larger than this number. To size a context window, use
        /// <see cref="EstimatedArtifactTokens"/>.
        /// </summary>
        [JsonPropertyName("estimatedTokens")]
        public int EstimatedTokens { get; set; }

        /// <summary>
        /// Estimate of the whole emitted artifact (serialized length ÷ 4),
        /// including the identity/provenance framing <see cref="EstimatedTokens"/>
        /// excludes. Reported, never budgeted against.
        /// </summary>
        [JsonPropertyName("estimatedArtifactTokens")]
        public int EstimatedArtifactTokens { get; set; }

        /// <summary>
        /// Advisory block restating <see cref="EstimatedTokens"/> and
        /// <see cref="EstimatedArtifactTokens"/> under names that carry their
        /// roles, so a consumer reading only the JSON payload : without
        /// <c>--help</c> or the summary line : cannot mistake the budget basis
        /// for the delivery size. Computed from the two integer fields, so it
        /// is always in sync with them; <see cref="CapsuleBudgetEnforcer"/>'s
        /// artifact-estimate fixed point converges on the full serialization
        /// including this block.
        /// </summary>
        [JsonPropertyName("tokenEstimate")]
        public TokenEstimateAdvisory TokenEstimate
            => new(EstimatedTokens, EstimatedArtifactTokens);

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
