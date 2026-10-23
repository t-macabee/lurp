using System.Text.Json;
using System.Text.Json.Nodes;
using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     B21 step 1: <see cref="SnapshotManifest.FromStorageManifest" /> and
///     <see cref="SnapshotManifestJson.WithBindingCompleteness" /> can be scoped to a set of
///     projects, and the binding rows, their summary and their total are scoped with them.
/// </summary>
public sealed class SnapshotManifestJsonTests
{
    private static readonly string[] _bothProjects = ["A", "B"];
    private static readonly string[] _projectA = ["A"];
    private static readonly string[] _projectC = ["C"];
    private static readonly string[] _bothDocuments = ["A/A.cs", "B/B.cs"];
    private static readonly string[] _aDocuments = ["A/A.cs"];
    private static readonly string[] _aIdentities = ["A1", "A2"];
    private static readonly string[] _bIdentities = ["B1"];

    [Fact]
    public void FromStorageManifest_NoProjectFilter_KeepsEveryProject()
    {
        var storage = BuildSnapshot();

        var manifest = SnapshotManifest.FromStorageManifest(storage);

        Assert.Equal(_bothProjects, manifest.TargetFrameworks.Keys);
        Assert.Equal(_bothProjects, manifest.ProjectGraph.Keys);
        Assert.Equal(_bothProjects, manifest.AssemblyNames.Keys);
        Assert.Equal(_bothProjects, manifest.MetadataReferenceIdentities.Keys);
        Assert.Equal(_bothProjects, manifest.CompilationOptionsFingerprints.Keys);
        Assert.Equal(_bothProjects, manifest.ProjectDocuments.Keys);
        Assert.NotNull(manifest.Completeness);
        Assert.Equal(_bothProjects, manifest.Completeness!.ActiveTfms.Keys);
        Assert.Equal(_bothDocuments, manifest.DocumentVersions.Keys.Select(k => k.RelativePath));
    }

    [Fact]
    public void FromStorageManifest_ProjectFilter_KeepsOnlyThoseProjects()
    {
        var storage = BuildSnapshot();

        var manifest = SnapshotManifest.FromStorageManifest(storage, new HashSet<string> { "A" });

        Assert.Equal(_projectA, manifest.TargetFrameworks.Keys);
        Assert.Equal(_projectA, manifest.ProjectGraph.Keys);
        Assert.Equal(_projectA, manifest.AssemblyNames.Keys);
        Assert.Equal(_projectA, manifest.MetadataReferenceIdentities.Keys);
        Assert.Equal(_projectA, manifest.CompilationOptionsFingerprints.Keys);
        Assert.Equal(_projectA, manifest.ProjectDocuments.Keys);
        Assert.NotNull(manifest.Completeness);
        Assert.Equal(_projectA, manifest.Completeness!.ActiveTfms.Keys);
        Assert.Equal(_aDocuments, manifest.DocumentVersions.Keys.Select(k => k.RelativePath));
    }

    [Fact]
    public void FromStorageManifest_SelectedProjectWithoutDocumentPaths_AddsNoDocuments()
    {
        var storage = BuildSnapshot();
        storage.Projects.Add(new ProjectRow
        {
            Name = "C",
            TargetFramework = "net10.0",
            DocumentPaths = null
        });

        var manifest = SnapshotManifest.FromStorageManifest(storage, new HashSet<string> { "C" });

        Assert.Equal(_projectC, manifest.TargetFrameworks.Keys);
        Assert.Empty(manifest.DocumentVersions);
        Assert.False(manifest.ProjectDocuments.ContainsKey("C"));
    }

    [Fact]
    public void WithBindingCompleteness_ProjectFilter_ScopesRowsSummaryAndTotal()
    {
        var storage = BuildSnapshot();
        var store = new StubBindingIncompletenessStore(
        [
            new BindingIncompletenessRecord("A", "A/A.cs", "unresolved_type", 3, "test"),
            new BindingIncompletenessRecord("B", "B/B.cs", "unresolved_type", 5, "test")
        ]);

        var manifest = SnapshotManifestJson.WithBindingCompleteness(
            store, storage, includeDetail: true, projects: new HashSet<string> { "A" });

        Assert.NotNull(manifest.Completeness);
        var completeness = manifest.Completeness!;
        Assert.All(completeness.BindingIncompleteness, record => Assert.Equal("A", record.ProjectName));
        Assert.All(completeness.BindingIncompletenessSummary, summary => Assert.Equal("A", summary.ProjectName));
        Assert.Equal(3, completeness.BindingIncompletenessTotal);
    }

    [Fact]
    public void FitToSize_FirstStepFits_RecordsOneEntryAndCallsFitsOnce()
    {
        var manifest = BuildManifestJson();
        var calls = 0;

        var entries = SnapshotManifestJson.FitToSize(manifest, Fits, "the note");

        Assert.Single(entries);
        Assert.Equal(1, calls);
        Assert.Equal("metadata_reference_identities", entries[0].Category);
        Assert.Equal("summarized", entries[0].Reason);

        bool Fits()
        {
            calls++;
            return true;
        }
    }

    [Fact]
    public void FitToSize_PicksLargestPartFirst_AndLeavesSmallerPartInFull()
    {
        var manifest = BuildManifestJson();

        var entries = SnapshotManifestJson.FitToSize(manifest, () => true, "the note");

        Assert.Single(entries);
        Assert.Equal("metadata_reference_identities", entries[0].Category);
        Assert.True(manifest.ContainsKey("document_versions"));
        Assert.True(manifest.ContainsKey("project_graph"));
    }

    [Fact]
    public void FitToSize_SummarizesReferences_CountsAndTotalReplaceIdentities()
    {
        var manifest = BuildManifestJson();

        SnapshotManifestJson.FitToSize(manifest, () => true, "the note");

        Assert.False(manifest.ContainsKey("metadata_reference_identities"));
        var counts = Assert.IsType<JsonObject>(manifest["metadata_reference_counts"]);
        Assert.Equal(40, (int)counts["A"]!);
        Assert.Equal(40, (int)manifest["metadata_reference_total"]!);
    }

    [Fact]
    public void FitToSize_SummarizesBindingRows_EmptyArrayAndSummaryKept()
    {
        var manifest = BuildManifestJson(referenceCount: 1, rowCount: 40);

        var entries = SnapshotManifestJson.FitToSize(manifest, () => true, "the note");

        Assert.Equal("completeness.binding_incompleteness", Assert.Single(entries).Category);
        Assert.Equal("summarized", entries[0].Reason);
        var completeness = Assert.IsType<JsonObject>(manifest["completeness"]);
        var rows = Assert.IsType<JsonArray>(completeness["binding_incompleteness"]);
        Assert.Empty(rows);
        Assert.NotNull(completeness["binding_incompleteness_summary"]);
        Assert.NotNull(completeness["binding_incompleteness_total"]);
    }

    [Fact]
    public void FitToSize_RemovesRemovablePart_ReasonBudgetExhausted()
    {
        var manifest = BuildManifestJson(referenceCount: 1, rowCount: 1);
        var paths = new JsonArray();
        for (var i = 0; i < 40; i++)
            paths.Add($"A/Project/File{i:D3}.cs");
        manifest["project_documents"] = new JsonObject { ["A"] = paths };

        var entries = SnapshotManifestJson.FitToSize(manifest, () => true, "the note");

        Assert.Equal("project_documents", Assert.Single(entries).Category);
        Assert.Equal("budget_exhausted", entries[0].Reason);
        Assert.False(manifest.ContainsKey("project_documents"));
    }

    [Fact]
    public void FitToSize_NeverFits_ProcessesEveryPartAndKeepsScalars()
    {
        var manifest = BuildManifestJson(referenceCount: 5, rowCount: 5);

        var entries = SnapshotManifestJson.FitToSize(manifest, () => false, "the note");

        string[] expected =
        [
            "document_versions",
            "metadata_reference_identities",
            "completeness.binding_incompleteness",
            "project_documents",
            "project_graph",
            "compilation_options_fingerprints",
            "assembly_names",
            "metadata_reference_counts",
            "completeness.active_tfms",
            "completeness.binding_incompleteness_summary"
        ];
        Assert.Equal(10, entries.Count);
        Assert.Equal(
            expected.OrderBy(name => name, StringComparer.Ordinal),
            entries.Select(entry => entry.Category).OrderBy(name => name, StringComparer.Ordinal));

        Assert.Equal("snap-1", (string)manifest["snapshot_id"]!);
        Assert.NotNull(manifest["target_frameworks"]);
        Assert.NotNull(manifest["document_count"]);
        Assert.NotNull(manifest["metadata_reference_total"]);
    }

    [Fact]
    public void FitToSize_NoteAndEntries_HaveTheNoteAndExactEntryShape()
    {
        var manifest = BuildManifestJson(referenceCount: 5, rowCount: 5);

        var entries = SnapshotManifestJson.FitToSize(manifest, () => false, "the note");

        Assert.Equal("the note", (string)manifest["omitted_note"]!);
        var omitted = Assert.IsType<JsonArray>(manifest["omitted_fields"]);
        Assert.Equal(entries.Count, omitted.Count);
        foreach (var entry in omitted)
        {
            var obj = Assert.IsType<JsonObject>(entry);
            Assert.Equal(2, obj.Count);
            Assert.True(obj.ContainsKey("category"));
            Assert.True(obj.ContainsKey("reason"));
        }
    }

    private static JsonObject BuildManifestJson(int referenceCount = 40, int rowCount = 2)
    {
        var identities = new JsonArray();
        for (var i = 0; i < referenceCount; i++)
            identities.Add($"Project.Ref{i:D3}");

        var rows = new JsonArray();
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(new JsonObject
            {
                ["project"] = "A",
                ["path"] = $"A/File{i:D3}.cs",
                ["reason"] = "unresolved_type",
                ["count"] = 1
            });
        }

        return new JsonObject
        {
            ["snapshot_id"] = "snap-1",
            ["target_frameworks"] = new JsonObject { ["A"] = "net10.0" },
            ["document_versions"] = new JsonObject { ["A/A.cs"] = "hash-a" },
            ["metadata_reference_identities"] = new JsonObject { ["A"] = identities },
            ["project_documents"] = new JsonObject { ["A"] = new JsonArray { "A/A.cs" } },
            ["project_graph"] = new JsonObject { ["A"] = new JsonArray { "B" } },
            ["compilation_options_fingerprints"] = new JsonObject { ["A"] = "fp-a" },
            ["assembly_names"] = new JsonObject { ["A"] = "AssemblyA" },
            ["completeness"] = new JsonObject
            {
                ["active_tfms"] = new JsonObject { ["A"] = "net10.0" },
                ["binding_incompleteness"] = rows,
                ["binding_incompleteness_summary"] = new JsonArray
                {
                    new JsonObject { ["project"] = "A", ["reason"] = "unresolved_type", ["count"] = 1 }
                },
                ["binding_incompleteness_total"] = 1
            }
        };
    }

    private static SnapshotRow BuildSnapshot()
    {
        return new SnapshotRow
        {
            SnapshotId = Guid.NewGuid().ToString(),
            WorkspaceId = "workspace:///root/Test.slnx",
            GitRoot = "/root",
            SolutionPath = "/root/Test.slnx",
            SdkVersion = "10.0.100",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            ToolVersion = VersionConstants.ToolVersion,
            Documents =
            [
                new() { DocumentId = "A/A.cs", FilePath = "A/A.cs", ContentHash = "hash-a" },
                new() { DocumentId = "B/B.cs", FilePath = "B/B.cs", ContentHash = "hash-b" }
            ],
            Projects =
            [
                new ProjectRow
                {
                    Name = "A",
                    TargetFramework = "net10.0",
                    AssemblyName = "A",
                    References = ["B"],
                    MetadataReferenceIdentitiesJson = JsonSerializer.Serialize(_aIdentities),
                    CompilationOptionsFingerprint = "fp-a",
                    DocumentPaths = ["A/A.cs"]
                },
                new ProjectRow
                {
                    Name = "B",
                    TargetFramework = "net10.0",
                    AssemblyName = "B",
                    MetadataReferenceIdentitiesJson = JsonSerializer.Serialize(_bIdentities),
                    CompilationOptionsFingerprint = "fp-b",
                    DocumentPaths = ["B/B.cs"]
                }
            ]
        };
    }

    [Fact]
    public void WorkspaceIdConverter_ValueOnly_ThrowsJsonException()
    {
        const string json = """
            {
              "snapshot_id": "00000000000000000000000000000000",
              "workspace_id": {
                "value": "workspace://C:/repo/App.sln"
              }
            }
            """;

        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<SnapshotManifest>(json, LurpJsonOptions.SnakeCaseIndented));

        Assert.Contains("git_root/solution_path", ex.Message);
        Assert.Contains("--mode=index --strategy=full", ex.Message);
    }

    [Fact]
    public void WorkspaceIdConverter_FullFields_Reads()
    {
        const string json = """
            {
              "snapshot_id": "00000000000000000000000000000000",
              "workspace_id": {
                "git_root": "/home/someone/repo",
                "solution_path": "/home/someone/repo/App.sln",
                "value": "workspace:///home/someone/repo/App.sln"
              }
            }
            """;

        var manifest = JsonSerializer.Deserialize<SnapshotManifest>(json, LurpJsonOptions.SnakeCaseIndented);

        Assert.NotNull(manifest);
        Assert.Equal("/home/someone/repo", manifest!.WorkspaceId.GitRoot);
        Assert.Equal("workspace:///home/someone/repo/App.sln", manifest.WorkspaceId.Value);
    }

    private sealed class StubBindingIncompletenessStore(IReadOnlyList<BindingIncompletenessRecord> records)
        : IBindingIncompletenessStore
    {
        public void SaveBindingIncompleteness(string snapshotId, IEnumerable<BindingIncompletenessRecord> recordsToSave) =>
            throw new NotSupportedException();

        public List<BindingIncompletenessRecord> GetBindingIncompleteness(string snapshotId, string? projectName = null) =>
            projectName == null ? [.. records] : [.. records.Where(record => record.ProjectName == projectName)];

        public void CopyBindingIncompleteness(string fromSnapshotId, string toSnapshotId) =>
            throw new NotSupportedException();

        public void DeleteBindingIncompletenessByDocumentPaths(string snapshotId, IEnumerable<string> documentPaths, string projectName) =>
            throw new NotSupportedException();
    }
}
