using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

/// <summary>
/// Contract tests for surfacing persisted binding-incompleteness as bounded,
/// reason-distinguished capsule uncertainties scoped to the anchor's documents
/// and traversed paths.
/// </summary>
public sealed class BindingIncompletenessUncertaintyTests : IDisposable
{
    private const string SnapshotId = "snap-binding-uncertainty";
    private const string AnchorId = "T:App.Service|prod";
    private const string TargetId = "T:App.IService|prod";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"binding_incompleteness_uncertainty_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void TraversedPathBindingIncompleteness_SurfacesReasonDistinguishedUncertainties()
    {
        var store = CreateSeededStore();
        // src/Service.cs is in scope because it is the source document of the
        // anchor's outgoing path. src/Other.cs is out of scope; the Tests
        // project has no in-scope document, so its project-level row stays out.
        store.SaveBindingIncompleteness(SnapshotId,
        [
            new BindingIncompletenessRecord("App", "src/Service.cs", BindingIncompletenessReason.CompilerError, 5, "v1"),
            new BindingIncompletenessRecord("App", "src/Service.cs", BindingIncompletenessReason.UnresolvedMetadata, 3, "v1"),
            new BindingIncompletenessRecord("App", "src/Other.cs", BindingIncompletenessReason.CompilerError, 9, "v1"),
            new BindingIncompletenessRecord("Tests", null, BindingIncompletenessReason.FilteredExternal, 2, "v1"),
        ]);

        var capsule = Assemble(store);

        Assert.NotNull(capsule.Completeness);
        Assert.Equal(19, capsule.Completeness.BindingIncompletenessTotal);

        var entries = capsule.Uncertainties
            .Where(entry => entry.RelationshipKind == "binding_incompleteness")
            .ToList();
        Assert.Equal(2, entries.Count);

        // compiler_error and unresolved_metadata are distinguished and both
        // state that relations MAY be missing rather than that source is absent.
        var compiler = Assert.Single(entries, entry => entry.Description.Contains("compiler errors", StringComparison.Ordinal));
        Assert.Contains("5", compiler.Description);
        Assert.Contains("App", compiler.Description);
        Assert.Contains("may be missing", compiler.Description);

        var metadata = Assert.Single(entries, entry => entry.Description.Contains("could not be resolved against project metadata", StringComparison.Ordinal));
        Assert.Contains("3", metadata.Description);
        Assert.Contains("may not be persisted", metadata.Description);
        Assert.Contains("even though the references exist in source", metadata.Description);

        // Out-of-scope rows are not surfaced.
        Assert.DoesNotContain(entries, entry => entry.Description.Contains("Other.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Description.Contains("9", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, entry => entry.Description.Contains("Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectLevelIncompleteness_ForInScopeProject_IsSurfaced()
    {
        var store = CreateSeededStore();
        store.SaveBindingIncompleteness(SnapshotId,
        [
            new BindingIncompletenessRecord("App", "src/Service.cs", BindingIncompletenessReason.CompilerError, 2, "v1"),
            new BindingIncompletenessRecord("App", null, BindingIncompletenessReason.ExtractorFailure, 1, "v1"),
            new BindingIncompletenessRecord("Tests", null, BindingIncompletenessReason.FilteredExternal, 7, "v1"),
        ]);

        var capsule = Assemble(store);

        var entries = capsule.Uncertainties
            .Where(entry => entry.RelationshipKind == "binding_incompleteness")
            .ToList();
        Assert.Equal(2, entries.Count);

        var extractorFailure = Assert.Single(entries, entry => entry.Description.Contains("extractor failure", StringComparison.Ordinal));
        Assert.Contains("1", extractorFailure.Description);

        // The Tests project-level row is not surfaced: no in-scope document
        // belongs to the Tests project.
        Assert.DoesNotContain(entries, entry => entry.Description.Contains("Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfScopeBindingIncompleteness_IsNotSurfaced()
    {
        var store = CreateSeededStore();
        store.SaveBindingIncompleteness(SnapshotId,
        [
            new BindingIncompletenessRecord("App", "src/Other.cs", BindingIncompletenessReason.CompilerError, 9, "v1"),
        ]);

        var capsule = Assemble(store);

        Assert.DoesNotContain(capsule.Uncertainties,
            entry => entry.RelationshipKind == "binding_incompleteness");
    }

    private static ContextCapsule Assemble(SqliteIndexStore store)
        => new ContextAssembler
        {
            EdgeStore = store,
            DeclarationStore = store,
            SnapshotId = SnapshotId,
            SymbolId = SymbolId.Parse(AnchorId),
            Intent = ContextIntent.Inspect,
            Budget = 100_000,
            MaxHops = 3,
            IncludeGenerated = false,
        }.Assemble();

    private SqliteIndexStore CreateSeededStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        SeedFkReferences();
        _store.SaveDeclarations(SnapshotId,
        [
            new SymbolDeclaration
            {
                SymbolId = SymbolId.Parse(AnchorId),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-v-binding-uncertainty",
                FullSpan = new DeclarationSpan(null, null),
                SignatureSpan = new DeclarationSpan(null, null),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(null, null),
                MetadataJson = """{"accessibility":"Public"}""",
            },
            new SymbolDeclaration
            {
                SymbolId = SymbolId.Parse(TargetId),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-v-binding-uncertainty",
                FullSpan = new DeclarationSpan(null, null),
                SignatureSpan = new DeclarationSpan(null, null),
                BodySpan = new DeclarationSpan(null, null),
                NameSpan = new DeclarationSpan(null, null),
                MetadataJson = """{"accessibility":"Public"}""",
            },
        ]);
        _store.SaveEdges(SnapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = AnchorId,
                TargetSymbolId = TargetId,
                Kind = EdgeKind.Calls.ToString(),
                Provenance = Provenance.CompilerProved,
                SnapshotId = SnapshotId,
                ExtractorVersion = "v1",
                SourceDocumentPath = "src/Service.cs",
                SourceStartLine = 12,
                SourceStartColumn = 5,
                SourceEndLine = 12,
                SourceEndColumn = 20,
            },
        ]);
        return _store;
    }

    private void SeedFkReferences()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-binding-uncertainty', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-binding-uncertainty', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-binding-uncertainty', 'test.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc-v-binding-uncertainty', 'doc-binding-uncertainty', 'hash');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        cmd.ExecuteNonQuery();
    }
}
