using System.Text.Json;
using System.Text.Json.Serialization;
using Lurp.Storage;

namespace Lurp.Workspace;

public sealed partial class SnapshotManifest
{

    [JsonPropertyName("snapshotId")]
    [JsonConverter(typeof(SnapshotIdConverter))]
    public SnapshotId SnapshotId { get; init; }

    [JsonPropertyName("workspaceId")]
    [JsonConverter(typeof(WorkspaceIdConverter))]
    public WorkspaceId WorkspaceId { get; init; }

    [JsonPropertyName("builtAtUtc")]
    public DateTime BuiltAtUtc { get; init; }

    [JsonPropertyName("documentVersions")]
    [JsonConverter(typeof(DocumentVersionMapConverter))]
    public Dictionary<DocumentId, DocumentVersionId> DocumentVersions { get; init; }
        = [];

    [JsonPropertyName("sdkVersion")]
    public string SdkVersion { get; init; } = "";

    [JsonPropertyName("compilerVersion")]
    public string CompilerVersion { get; init; } = "";

    [JsonPropertyName("targetFrameworks")]
    public Dictionary<string, string> TargetFrameworks { get; init; }
        = [];

    [JsonPropertyName("projectGraph")]
    public Dictionary<string, string[]> ProjectGraph { get; init; }
        = [];

    [JsonPropertyName("databaseSchemaVersion")]
    public int DatabaseSchemaVersion { get; init; }

    [JsonPropertyName("outputSchemaVersion")]
    public int OutputSchemaVersion { get; init; }

    [JsonPropertyName("extractorVersion")]
    public string ExtractorVersion { get; init; } = "";

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; init; } = "";

    [JsonPropertyName("previousSnapshotId")]
    [JsonConverter(typeof(NullableSnapshotIdConverter))]
    public SnapshotId? PreviousSnapshotId { get; init; }

    [JsonPropertyName("completeness")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnapshotCompleteness? Completeness { get; set; }

    public static SnapshotManifest FromWorkspace(WorkspaceInfo workspace,SnapshotId snapshotId,SnapshotId? previousSnapshotId = null,IReadOnlySet<string>? skipAdapters = null)
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
            ProjectGraph = workspace.ProjectGraph.ToDictionary(kvp => kvp.Key,kvp => kvp.Value.OrderBy(x => x).ToArray(),
                StringComparer.Ordinal),
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
                ExtractorVersion = VersionConstants.ExtractorVersion,
            },
        };
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public void Save(ISnapshotStore snapshotStore, IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents = null,
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

    internal Storage.SnapshotRow ToStorageManifest(IReadOnlyDictionary<DocumentId, (byte[] Content, string Encoding, string LineStarts)>? contents = null)
    {
        var documents = DocumentVersions.Select(kvp =>{var docId = kvp.Key;var docPath = docId.ToString();
            byte[]? content = null;
            string encoding = "";
            string lineStarts = "";

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
                LineStart = lineStarts,
                CreatedAtUtc = DateTime.MinValue,
                LineStarts = lineStarts,
            };
        }).ToList();

        var projects = TargetFrameworks.Select(kvp => new Storage.ProjectRow
        {
            Name = kvp.Key,
            TargetFramework = kvp.Value,
            References = ProjectGraph.TryGetValue(kvp.Key, out var refs)
                ? refs.OrderBy(x => x, StringComparer.Ordinal).ToList()
                : [],
        }).ToList();

        return new Storage.SnapshotRow
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
            SkippedAdapters = Completeness?.SkippedAdapters ?? [],
        };
    }

    internal static SnapshotManifest FromStorageManifest(Storage.SnapshotRow storage)
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
        foreach (var project in storage.Projects)
        {
            targetFrameworks[project.Name] = project.TargetFramework;
            projectGraph[project.Name] = project.References.ToArray();
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
                ExtractorVersion = storage.ExtractorVersion,
            },
        };
    }

}

