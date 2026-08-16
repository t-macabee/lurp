using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lurp.Workspace;

public sealed partial class SnapshotManifest
{
    private static readonly JsonSerializerOptions _jsonOptions = LurpJsonOptions.SnakeCaseIndented;

    [JsonPropertyName("snapshot_id")]
    [JsonConverter(typeof(SnapshotIdConverter))]
    public SnapshotId SnapshotId { get; init; }

    [JsonPropertyName("workspace_id")]
    [JsonConverter(typeof(WorkspaceIdConverter))]
    public WorkspaceId WorkspaceId { get; init; }

    [JsonPropertyName("built_at_utc")] public DateTime BuiltAtUtc { get; init; }

    [JsonPropertyName("document_versions")]
    [JsonConverter(typeof(DocumentVersionMapConverter))]
    public Dictionary<DocumentId, DocumentVersionId> DocumentVersions { get; init; }
        = [];

    [JsonPropertyName("sdk_version")] public string SdkVersion { get; init; } = "";

    [JsonPropertyName("compiler_version")] public string CompilerVersion { get; init; } = "";

    [JsonPropertyName("target_frameworks")]
    public Dictionary<string, string> TargetFrameworks { get; init; }
        = [];

    [JsonPropertyName("project_graph")]
    public Dictionary<string, string[]> ProjectGraph { get; init; }
        = [];

    [JsonPropertyName("metadata_reference_identities")]
    public IReadOnlyDictionary<string, ImmutableArray<string>> MetadataReferenceIdentities { get; init; }
        = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);

    [JsonPropertyName("compilation_options_fingerprints")]
    public IReadOnlyDictionary<string, string> CompilationOptionsFingerprints { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("database_schema_version")]
    public int DatabaseSchemaVersion { get; init; }

    [JsonPropertyName("output_schema_version")]
    public int OutputSchemaVersion { get; init; }

    [JsonPropertyName("extractor_version")]
    public string ExtractorVersion { get; init; } = "";

    [JsonPropertyName("tool_version")] public string ToolVersion { get; init; } = "";

    [JsonPropertyName("previous_snapshot_id")]
    [JsonConverter(typeof(NullableSnapshotIdConverter))]
    public SnapshotId? PreviousSnapshotId { get; init; }

    [JsonPropertyName("completeness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapshotCompleteness? Completeness { get; set; }

    public static SnapshotManifest FromWorkspace(WorkspaceInfo workspace, SnapshotId snapshotId, SnapshotId? previousSnapshotId = null, IReadOnlySet<string>? skipAdapters = null)
    {
        return new SnapshotManifest
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspace.Id,
            BuiltAtUtc = DateTime.UtcNow,
            DocumentVersions = new Dictionary<DocumentId, DocumentVersionId>(workspace.Documents),
            SdkVersion = workspace.SdkVersion,
            CompilerVersion = workspace.CompilerVersion.ToString(),
            TargetFrameworks = new Dictionary<string, string>(workspace.TargetFrameworks),
            ProjectGraph = workspace.ProjectGraph.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OrderBy(x => x).ToArray(),
                StringComparer.Ordinal),
            MetadataReferenceIdentities = new Dictionary<string, ImmutableArray<string>>(workspace.MetadataReferenceIdentities, StringComparer.Ordinal),
            CompilationOptionsFingerprints = new Dictionary<string, string>(workspace.CompilationOptionsFingerprints, StringComparer.Ordinal),
            DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            ToolVersion = VersionConstants.ToolVersion,
            PreviousSnapshotId = previousSnapshotId,
            Completeness = new SnapshotCompleteness
            {
                GeneratedTreesIncluded = false,
                SkippedAdapters = skipAdapters != null
                    ? skipAdapters.OrderBy(x => x, StringComparer.Ordinal).ToList()
                    : [],
                ActiveTfms = new Dictionary<string, string>(workspace.TargetFrameworks),
                ExtractorVersion = VersionConstants.ExtractorVersion
            }
        };
    }

    public void Save(ISnapshotManifestStore snapshotStore, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents = null,
        string? jsonExportPath = null)
    {
        if (snapshotStore == null)
            throw new ArgumentNullException(nameof(snapshotStore));

        snapshotStore.SaveSnapshot(ToStorageManifest(contents));

        if (jsonExportPath != null)
        {
            var json = JsonSerializer.Serialize(this, _jsonOptions);
            File.WriteAllText(jsonExportPath, json);
        }
    }

    public static SnapshotManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SnapshotManifest>(json, _jsonOptions)
               ?? throw new InvalidOperationException("Failed to deserialize snapshot manifest.");
    }

    internal SnapshotRow ToStorageManifest(IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents = null)
    {
        var documents = DocumentVersions.Select(kvp =>
        {
            var docId = kvp.Key;
            var docPath = docId.ToString();
            byte[]? content = null;
            var encoding = "";
            var lineStarts = "";

            if (contents != null && contents.TryGetValue(docId, out var entry))
            {
                content = entry.Content;
                encoding = entry.Encoding;
                lineStarts = entry.LineStarts;
            }

            return new DocumentVersion(content ?? Array.Empty<byte>())
            {
                DocumentId = docPath,
                FilePath = docPath,
                ContentHash = kvp.Value.Hash,
                Encoding = encoding,
                LineStarts = lineStarts
            };
        }).ToList();

        var projects = TargetFrameworks.Select(kvp => new ProjectRow
        {
            Name = kvp.Key,
            TargetFramework = kvp.Value,
            References = ProjectGraph.TryGetValue(kvp.Key, out var refs)
                ? refs.OrderBy(x => x, StringComparer.Ordinal).ToList()
                : [],
            MetadataReferenceIdentitiesJson = MetadataReferenceIdentities.TryGetValue(kvp.Key, out var ids)
                ? JsonSerializer.Serialize(ids)
                : null,
            CompilationOptionsFingerprint = CompilationOptionsFingerprints.TryGetValue(kvp.Key, out var fp)
                ? fp
                : null
        }).ToList();

        return new SnapshotRow
        {
            SnapshotId = SnapshotId.ToString(),
            WorkspaceId = WorkspaceId.Value,
            GitRoot = WorkspaceId.GitRoot,
            SolutionPath = WorkspaceId.SolutionPath,
            SdkVersion = SdkVersion,
            CompilerVersion = CompilerVersion,
            CreatedAtUtc = BuiltAtUtc,
            Documents = documents,
            DatabaseSchemaVersion = DatabaseSchemaVersion,
            OutputSchemaVersion = OutputSchemaVersion,
            ExtractorVersion = ExtractorVersion,
            ToolVersion = ToolVersion,
            PreviousSnapshotId = PreviousSnapshotId?.ToString(),
            Projects = projects,
            SkippedAdapters = Completeness?.SkippedAdapters ?? []
        };
    }

    internal static SnapshotManifest FromStorageManifest(SnapshotRow storage)
    {
        var documentVersions = new Dictionary<DocumentId, DocumentVersionId>();
        foreach (var doc in storage.Documents)
        {
            var docId = new DocumentId(doc.FilePath);
            var versionId = new DocumentVersionId(doc.FilePath, doc.ContentHash);
            documentVersions[docId] = versionId;
        }

        var targetFrameworks = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectGraph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var metadataReferenceIdentities = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        var compilationOptionsFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in storage.Projects)
        {
            targetFrameworks[project.Name] = project.TargetFramework;
            projectGraph[project.Name] = project.References.ToArray();
            if (project.MetadataReferenceIdentitiesJson != null)
                metadataReferenceIdentities[project.Name] = JsonSerializer
                    .Deserialize<string[]>(project.MetadataReferenceIdentitiesJson)!
                    .ToImmutableArray();
            if (project.CompilationOptionsFingerprint != null)
                compilationOptionsFingerprints[project.Name] = project.CompilationOptionsFingerprint;
        }

        return new SnapshotManifest
        {
            SnapshotId = SnapshotId.Parse(storage.SnapshotId),
            WorkspaceId = WorkspaceId.Create(storage.GitRoot, storage.SolutionPath),
            BuiltAtUtc = storage.CreatedAtUtc,
            DocumentVersions = documentVersions,
            SdkVersion = storage.SdkVersion,
            CompilerVersion = storage.CompilerVersion,
            TargetFrameworks = targetFrameworks,
            ProjectGraph = projectGraph,
            MetadataReferenceIdentities = metadataReferenceIdentities,
            CompilationOptionsFingerprints = compilationOptionsFingerprints,
            DatabaseSchemaVersion = storage.DatabaseSchemaVersion,
            OutputSchemaVersion = storage.OutputSchemaVersion,
            ExtractorVersion = storage.ExtractorVersion,
            ToolVersion = storage.ToolVersion,
            PreviousSnapshotId = storage.PreviousSnapshotId != null
                ? SnapshotId.Parse(storage.PreviousSnapshotId)
                : null,
            Completeness = new SnapshotCompleteness
            {
                GeneratedTreesIncluded = false,
                SkippedAdapters = storage.SkippedAdapters,
                ActiveTfms = new Dictionary<string, string>(targetFrameworks),
                ExtractorVersion = storage.ExtractorVersion
            }
        };
    }
}