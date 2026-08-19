using System.Text.Json.Serialization;

namespace Lurp.Storage;

public class SnapshotRow
{
    public string SnapshotId { get; init; } = string.Empty;
    public string WorkspaceId { get; init; } = string.Empty;
    public string GitRoot { get; init; } = string.Empty;
    public string SolutionPath { get; init; } = string.Empty;
    public string SdkVersion { get; init; } = string.Empty;
    public string CompilerVersion { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public List<DocumentVersion> Documents { get; init; } = [];
    public int DatabaseSchemaVersion { get; init; }
    public int OutputSchemaVersion { get; init; }
    public string ExtractorVersion { get; init; } = string.Empty;
    public string ToolVersion { get; init; } = string.Empty;
    public string? PreviousSnapshotId { get; init; }
    public List<ProjectRow> Projects { get; init; } = [];
    public List<string> SkippedAdapters { get; init; } = [];
}

public sealed class ProjectRow
{
    public string Name { get; init; } = string.Empty;
    public string TargetFramework { get; init; } = string.Empty;
    public List<string> References { get; init; } = [];
    public string? MetadataReferenceIdentitiesJson { get; init; }
    public string? CompilationOptionsFingerprint { get; init; }
}

public enum GraphNodeKind
{
    Route,
    Convention,
    RuntimePlaceholder,
    ExternalType
}

public sealed record OrphanEdgeDropSummary(int Total, int External, int CompilerSynthesized, int Other)
{
    public static readonly OrphanEdgeDropSummary Empty = new(0, 0, 0, 0);

    public string FormatDropSummary()
    {
        return $"{Total} (external={External}, compiler_synthesized={CompilerSynthesized}, other={Other})";
    }

    public string? FormatWarning()
    {
        return Other > 0
            ? $"  ⚠ edges_dropped_reason_other: {Other} — investigate (endpoints outside declared scope for no known reason)"
            : null;
    }
}

public sealed class EdgeRecord
{
    public string SourceSymbolId { get; init; } = string.Empty;
    public string TargetSymbolId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Provenance { get; init; } = string.Empty;
    public string SnapshotId { get; init; } = string.Empty;
    public string ExtractorVersion { get; init; } = string.Empty;
    public string? SourceDocumentPath { get; init; }

    /// <summary>
    ///     0-based (Roslyn-native). Storage keeps the raw
    ///     <c>LinePosition.Line</c>; convert with <see cref="LineNumbers.ToOneBased(int?)" />
    ///     before this value reaches a consumer. See LineNumbers.cs (audit T4).
    /// </summary>
    public int? SourceStartLine { get; init; }

    public int? SourceStartColumn { get; init; }

    /// <summary>0-based (Roslyn-native) — see <see cref="SourceStartLine" />.</summary>
    public int? SourceEndLine { get; init; }

    public int? SourceEndColumn { get; init; }
    public bool IsCrossGenerated { get; init; }
    public string? TypeArgumentsJson { get; init; }
    public string? ReceiverTypeConstraintsJson { get; init; }
    public GraphNodeKind? SourceNodeKind { get; init; }
    public GraphNodeKind? TargetNodeKind { get; init; }
}

public sealed class DiagnosticRecord
{
    public string ProjectName { get; init; } = string.Empty;
    public string? DocumentPath { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public int? StartLine { get; init; }
    public int? StartColumn { get; init; }
    public int? EndLine { get; init; }
    public int? EndColumn { get; init; }
}

/// <summary>
///     Read-surface wrapper around <see cref="DiagnosticRecord" /> that adds
///     computed fields (<see cref="InSnapshot" />) without polluting the
///     index-time extraction type. The <see cref="DocumentPath" /> on this
///     record is the normalized git-relative path, not the raw stored value.
/// </summary>
public sealed record DiagnosticEntry(DiagnosticRecord Record, string? NormalizedDocumentPath, bool? InSnapshot);

public sealed record BindingIncompletenessRecord(
    [property: JsonPropertyName("project_name")]
    string ProjectName,
    [property: JsonPropertyName("document_path")]
    string? DocumentPath,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("extractor_version")]
    string ExtractorVersion);

public sealed record BindingIncompletenessSummary(
    [property: JsonPropertyName("project_name")]
    string ProjectName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("count")] int Count);

public sealed class AnnotationRecord
{
    public AnnotationRecord(string symbolId, string kind, string value, string? documentPath = null)
    {
        SymbolId = symbolId ?? throw new ArgumentNullException(nameof(symbolId));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        Value = value ?? throw new ArgumentNullException(nameof(value));
        DocumentPath = documentPath;
    }

    public string SymbolId { get; }
    public string Kind { get; }
    public string Value { get; }

    /// <summary>
    ///     Git-root-relative path of the document whose walk produced the
    ///     annotation, or null for user-authored annotations (which are never
    ///     retired by the incremental path-scoped delete).
    /// </summary>
    public string? DocumentPath { get; }
}

/// <summary>
///     Persisted TEXT change-type tokens (Migration_007). These are write-mostly:
///     emitted verbatim to JSON output (<c>DiffHandler</c>, <c>ImpactHandler</c>)
///     and never parsed back into a typed switch — per §5.6, unknown enum values are
///     survivable input. Producers and comparators reference these compiler-checked
///     constants, not raw literals.
/// </summary>
public static class ChangeType
{
    public const string SymbolAdded = "symbol_added";
    public const string SymbolRemoved = "symbol_removed";
    public const string SymbolRenamed = "symbol_renamed";
    public const string SymbolMoved = "symbol_moved";
    public const string SymbolRelocated = "symbol_relocated";
    public const string AccessibilityChanged = "accessibility_changed";
    public const string SignatureChanged = "signature_changed";
    public const string BaseTypeChanged = "base_type_changed";
    public const string InterfacesChanged = "interfaces_changed";
    public const string RecordChanged = "record_changed";
    public const string MetadataChanged = "metadata_changed";
    public const string EdgeAdded = "edge_added";
    public const string EdgeRemoved = "edge_removed";
    public const string EdgeEvidenceChanged = "edge_evidence_changed";
    public const string EdgeLocationChanged = "edge_location_changed";
    public const string AttributeChanged = "attribute_changed";
    public const string BodyOnlyChanged = "body_only_changed";
    public const string ComparisonUnavailable = "comparison_unavailable";
}

public sealed class SemanticChange
{
    [JsonPropertyName("change_id")] public string ChangeId { get; init; } = string.Empty;

    [JsonPropertyName("from_snapshot_id")] public string FromSnapshotId { get; init; } = string.Empty;

    [JsonPropertyName("to_snapshot_id")] public string ToSnapshotId { get; init; } = string.Empty;

    [JsonPropertyName("change_type")] public string ChangeType { get; init; } = string.Empty;

    [JsonPropertyName("symbol_id")] public string SymbolId { get; init; } = string.Empty;

    [JsonPropertyName("detail_json")] public string? DetailJson { get; init; }

    [JsonPropertyName("created_at_utc")] public DateTime CreatedAtUtc { get; init; }
}

public sealed class SnapshotTimingRow
{
    public SnapshotTimingRow(string stepName, long elapsedMs)
    {
        StepName = stepName ?? throw new ArgumentNullException(nameof(stepName));
        ElapsedMs = elapsedMs;
    }

    public string StepName { get; }
    public long ElapsedMs { get; }
}

public sealed record SnapshotFailureRow(string SnapshotId, string ReasonCode, string? Message, DateTime CreatedAtUtc);

public class DocumentVersion
{
    public DocumentVersion()
    {
    }

    public DocumentVersion(byte[]? content)
    {
        Content = content;
    }

    public string DocumentId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public string Encoding { get; init; } = string.Empty;

    public byte[]? Content { get; }
    public int ByteCount => Content?.Length ?? 0;
    public string? LineStarts { get; init; }
}