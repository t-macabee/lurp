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

    [JsonIgnore]
    public IReadOnlySet<DocumentId> GeneratedDocuments { get; init; }
        = new HashSet<DocumentId>();

    [JsonPropertyName("sdk_version")] public string SdkVersion { get; init; } = "";

    [JsonPropertyName("compiler_version")] public string CompilerVersion { get; init; } = "";

    [JsonPropertyName("target_frameworks")]
    public Dictionary<string, string> TargetFrameworks { get; init; }
        = [];

    [JsonPropertyName("project_graph")]
    public Dictionary<string, string[]> ProjectGraph { get; init; }
        = [];

    [JsonPropertyName("assembly_names")]
    public Dictionary<string, string> AssemblyNames { get; init; }
        = [];

    [JsonPropertyName("metadata_reference_identities")]
    public IReadOnlyDictionary<string, ImmutableArray<string>> MetadataReferenceIdentities { get; init; }
        = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);

    [JsonPropertyName("compilation_options_fingerprints")]
    public IReadOnlyDictionary<string, string> CompilationOptionsFingerprints { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("internals_visible_to")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> InternalsVisibleTo { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyDictionary<string, string> ProjectPaths { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonIgnore]
    public IReadOnlyList<string> UnloadedProjects { get; init; } = [];

    [JsonPropertyName("project_documents")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectDocuments { get; init; }
        = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

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
            GeneratedDocuments = new HashSet<DocumentId>(workspace.GeneratedDocuments),
            SdkVersion = workspace.SdkVersion,
            CompilerVersion = workspace.CompilerVersion.ToString(),
            TargetFrameworks = new Dictionary<string, string>(workspace.TargetFrameworks),
            ProjectGraph = workspace.ProjectGraph.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.OrderBy(x => x).ToArray(),
                StringComparer.Ordinal),
            AssemblyNames = new Dictionary<string, string>(workspace.AssemblyNames, StringComparer.Ordinal),
            MetadataReferenceIdentities = new Dictionary<string, ImmutableArray<string>>(workspace.MetadataReferenceIdentities, StringComparer.Ordinal),
            CompilationOptionsFingerprints = new Dictionary<string, string>(workspace.CompilationOptionsFingerprints, StringComparer.Ordinal),
            InternalsVisibleTo = new Dictionary<string, IReadOnlyList<string>>(workspace.InternalsVisibleTo, StringComparer.Ordinal),
            ProjectPaths = new Dictionary<string, string>(workspace.ProjectPaths, StringComparer.Ordinal),
            UnloadedProjects = workspace.UnloadedProjects,
            ProjectDocuments = new Dictionary<string, IReadOnlyList<string>>(workspace.ProjectDocuments, StringComparer.Ordinal),
            DatabaseSchemaVersion = MigrationRunner.LatestVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            ToolVersion = VersionConstants.ToolVersion,
            PreviousSnapshotId = previousSnapshotId,
            Completeness = new SnapshotCompleteness
            {
                GeneratedTreesIncluded = false,
                SkippedAdapters = skipAdapters != null
                    ? [.. skipAdapters.OrderBy(x => x, StringComparer.Ordinal)]
                    : [],
                ActiveTfms = new Dictionary<string, string>(workspace.TargetFrameworks),
                ExtractorVersion = VersionConstants.ExtractorVersion
            }
        };
    }

    public void Save(SqliteIndexStore snapshotStore, string buildStrategy,
        IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents = null)
        => Save(snapshotStore, buildStrategy, contents, replaceExisting: false);

    internal void Save(SqliteIndexStore snapshotStore, string buildStrategy,
        IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents,
        bool replaceExisting, IReadOnlyDictionary<string, string>? buildInputs = null)
    {
        ArgumentNullException.ThrowIfNull(snapshotStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildStrategy);

        snapshotStore.SaveSnapshot(ToStorageManifest(buildStrategy, contents), replaceExisting, buildInputs);
    }

    internal void ExportJson(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, _jsonOptions));

    internal SnapshotRow ToStorageManifest(string buildStrategy, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents = null)
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

            return new DocumentVersion(content ?? [])
            {
                DocumentId = docPath,
                FilePath = docPath,
                ContentHash = kvp.Value.Hash,
                Encoding = encoding,
                LineStarts = lineStarts,
                IsGenerated = GeneratedDocuments.Contains(docId)
            };
        }).ToList();

        var projects = TargetFrameworks.Select(kvp => new ProjectRow
        {
            Name = kvp.Key,
            TargetFramework = kvp.Value,
            AssemblyName = AssemblyNames.TryGetValue(kvp.Key, out var assemblyName)
                ? assemblyName
                : kvp.Key,
            References = ProjectGraph.TryGetValue(kvp.Key, out var refs)
                ? [.. refs.OrderBy(x => x, StringComparer.Ordinal)]
                : [],
            MetadataReferenceIdentitiesJson = MetadataReferenceIdentities.TryGetValue(kvp.Key, out var ids)
                ? JsonSerializer.Serialize(ids)
                : null,
            CompilationOptionsFingerprint = CompilationOptionsFingerprints.TryGetValue(kvp.Key, out var fp)
                ? fp
                : null,
            DocumentPaths = ProjectDocuments.TryGetValue(kvp.Key, out var documentPaths)
                ? [.. documentPaths]
                : null,
            InternalsVisibleToJson = InternalsVisibleTo.TryGetValue(kvp.Key, out var ivt) && ivt.Count > 0
                ? JsonSerializer.Serialize(ivt)
                : null,
            ProjectPath = ProjectPaths.TryGetValue(kvp.Key, out var projectPath)
                ? projectPath
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
            UnloadedProjects = UnloadedProjects,
            SkippedAdapters = Completeness?.SkippedAdapters ?? [],
            BuildStrategy = buildStrategy
        };
    }

    /// <summary>
    ///     Rebuilds the rich manifest from a stored snapshot row. With <paramref name="projects" />,
    ///     the manifest holds only those projects, their documents and their TFMs.
    /// </summary>
    internal static SnapshotCompleteness StorageCompleteness(SnapshotRow storage, IEnumerable<ProjectRow> projects)
    {
        var targetFrameworks = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in projects)
            targetFrameworks[project.Name] = project.TargetFramework;

        return new SnapshotCompleteness
        {
            GeneratedTreesIncluded = false,
            SkippedAdapters = storage.SkippedAdapters,
            ActiveTfms = targetFrameworks,
            ExtractorVersion = storage.ExtractorVersion
        };
    }

    internal static SnapshotManifest FromStorageManifest(SnapshotRow storage, IReadOnlySet<string>? projects = null)
    {
        var selectedProjects = projects == null
            ? storage.Projects
            : storage.Projects.Where(project => projects.Contains(project.Name)).ToList();
        HashSet<string>? selectedDocuments = projects == null
            ? null
            : selectedProjects.SelectMany(project => project.DocumentPaths ?? []).ToHashSet(StringComparer.Ordinal);

        var documentVersions = new Dictionary<DocumentId, DocumentVersionId>();
        var generatedDocuments = new HashSet<DocumentId>();
        foreach (var doc in storage.Documents)
        {
            if (selectedDocuments != null && !selectedDocuments.Contains(doc.FilePath))
                continue;

            var docId = new DocumentId(doc.FilePath);
            var versionId = new DocumentVersionId(doc.FilePath, doc.ContentHash);
            documentVersions[docId] = versionId;
            if (doc.IsGenerated)
                generatedDocuments.Add(docId);
        }

        var completeness = StorageCompleteness(storage, selectedProjects);
        var projectGraph = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var assemblyNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var metadataReferenceIdentities = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal);
        var compilationOptionsFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var internalsVisibleTo = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var projectPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var projectDocuments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var project in selectedProjects)
        {
            projectGraph[project.Name] = [.. project.References];
            if (project.AssemblyName != null)
                assemblyNames[project.Name] = project.AssemblyName;
            if (project.MetadataReferenceIdentitiesJson != null)
                metadataReferenceIdentities[project.Name] = [.. JsonSerializer
                    .Deserialize<string[]>(project.MetadataReferenceIdentitiesJson)!];
            if (project.CompilationOptionsFingerprint != null)
                compilationOptionsFingerprints[project.Name] = project.CompilationOptionsFingerprint;
            if (project.InternalsVisibleToJson != null)
            {
                var targets = JsonSerializer.Deserialize<string[]>(project.InternalsVisibleToJson);
                if (targets is { Length: > 0 })
                    internalsVisibleTo[project.Name] = targets;
            }
            if (project.ProjectPath != null)
                projectPaths[project.Name] = project.ProjectPath;
            if (project.DocumentPaths != null)
                projectDocuments[project.Name] = project.DocumentPaths;
        }

        return new SnapshotManifest
        {
            SnapshotId = SnapshotId.Parse(storage.SnapshotId),
            WorkspaceId = WorkspaceId.Create(storage.GitRoot, storage.SolutionPath),
            BuiltAtUtc = storage.CreatedAtUtc,
            DocumentVersions = documentVersions,
            GeneratedDocuments = generatedDocuments,
            SdkVersion = storage.SdkVersion,
            CompilerVersion = storage.CompilerVersion,
            TargetFrameworks = new Dictionary<string, string>(completeness.ActiveTfms),
            ProjectGraph = projectGraph,
            AssemblyNames = assemblyNames,
            MetadataReferenceIdentities = metadataReferenceIdentities,
            CompilationOptionsFingerprints = compilationOptionsFingerprints,
            InternalsVisibleTo = internalsVisibleTo,
            ProjectPaths = projectPaths,
            UnloadedProjects = storage.UnloadedProjects,
            ProjectDocuments = projectDocuments,
            DatabaseSchemaVersion = storage.DatabaseSchemaVersion,
            OutputSchemaVersion = storage.OutputSchemaVersion,
            ExtractorVersion = storage.ExtractorVersion,
            ToolVersion = storage.ToolVersion,
            PreviousSnapshotId = storage.PreviousSnapshotId != null
                ? SnapshotId.Parse(storage.PreviousSnapshotId)
                : null,
            Completeness = completeness
        };
    }
}
