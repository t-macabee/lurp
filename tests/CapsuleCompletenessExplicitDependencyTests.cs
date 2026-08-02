using System.Text;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

/// <summary>
/// Regression tests for the explicit binding-incompleteness dependency of
/// capsule assembly. The completeness reader is a supplied dependency, never a
/// runtime capability check on the edge store: when the dependency is absent,
/// completeness is unavailable and no relation omission may be reported as a
/// proved "empty".
/// </summary>
public sealed class CapsuleCompletenessExplicitDependencyTests : IDisposable
{
    private const string SnapshotId = "snap-completeness-dependency";
    private const string AnchorId = "T:App.Service|prod";
    private const string AnchorDocument = "src/Service.cs";

    private static readonly byte[] Source = Encoding.UTF8.GetBytes(
        "namespace App\n{\n    public class Service\n    {\n    }\n}\n");

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"capsule_completeness_dependency_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Fact]
    public void IncompleteAnchor_EmitsUnresolved_DetailGated_SummaryAndTotalDeterministic()
    {
        var store = CreateSeededStore();
        var summary = Assemble(store, IncludeCompletenessDetail: false);

        // The anchor's document carries unobservable bindings (compiler_error),
        // so genuinely empty tiers must be omitted as "unresolved", never "empty".
        Assert.Contains(summary.OmittedTiers,
            entry => entry.Category == "contracts" && entry.Reason == "unresolved");
        Assert.Contains(summary.OmittedTiers,
            entry => entry.Category == "directCallees" && entry.Reason == "unresolved");
        Assert.Contains(summary.OmittedTiers,
            entry => entry.Category == "directCallers" && entry.Reason == "unresolved");
        Assert.DoesNotContain(summary.OmittedTiers, entry => entry.Reason == "empty");

        // Completeness is present (a reader was supplied), but detailed per-document
        // rows stay gated behind the detail option; summary and total are the
        // deterministic rollup and are not affected by the detail flag.
        Assert.NotNull(summary.Completeness);
        Assert.Empty(summary.Completeness.BindingIncompleteness);
        Assert.Equal(17, summary.Completeness.BindingIncompletenessTotal);
        Assert.Collection(summary.Completeness.BindingIncompletenessSummary,
            entry =>
            {
                Assert.Equal("App", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.CompilerError, entry.Reason);
                Assert.Equal(14, entry.Count);
            },
            entry =>
            {
                Assert.Equal("App", entry.ProjectName);
                Assert.Equal(BindingIncompletenessReason.UnresolvedMetadata, entry.Reason);
                Assert.Equal(3, entry.Count);
            });

        var detailed = Assemble(store, IncludeCompletenessDetail: true);
        Assert.NotNull(detailed.Completeness);
        Assert.Equal(3, detailed.Completeness.BindingIncompleteness.Count);
        Assert.Equal(17, detailed.Completeness.BindingIncompletenessTotal);
        Assert.Equal(
            summary.Completeness.BindingIncompletenessSummary,
            detailed.Completeness.BindingIncompletenessSummary);
    }

    [Fact]
    public void NoCompletenessReader_CompletenessUnavailable_AndEmptinessNeverProved()
    {
        // The seeded store itself implements IBindingIncompletenessStore; the
        // dependency is deliberately not supplied. If assembly sniffed the edge
        // store's capability instead of the explicit dependency, this test would
        // see completeness populated and "empty" reasons — the defect being fixed.
        var store = CreateSeededStore();
        var capsule = Assemble(store, supplyCompletenessReader: false);

        // The fallback is explicit: it does not throw...
        Assert.NotNull(capsule);

        // ...it does not fabricate records: completeness is unavailable...
        Assert.Null(capsule.Completeness);
        using var document = JsonDocument.Parse(ContextCapsuleJson.Serialize(capsule));
        Assert.False(document.RootElement.TryGetProperty("completeness", out _));

        // ...and it cannot emit "empty" as a proved absence: every empty tier is
        // marked "unresolved", including the non-tier affectedPublicSurfaces
        // channel when it is empty.
        Assert.NotEmpty(capsule.OmittedTiers);
        Assert.DoesNotContain(capsule.OmittedTiers, entry => entry.Reason == "empty");
        Assert.All(capsule.OmittedTiers, entry => Assert.Equal("unresolved", entry.Reason));
        Assert.Contains(capsule.OmittedTiers,
            entry => entry.Category == "contracts" && entry.Reason == "unresolved");
        Assert.Contains(capsule.OmittedTiers,
            entry => entry.Category == "registeredImplementations" && entry.Reason == "unresolved");

        // The in-band explanation is emitted, so consumers can read why the
        // omission channel is "unresolved" and not a proved absence.
        Assert.Contains(capsule.InclusionReasons.Keys, key => key == "omittedTiers.unresolved");

        // Supplying the same store as the explicit dependency restores
        // completeness: the fallback is keyed on the dependency, not the store.
        var withReader = Assemble(store, supplyCompletenessReader: true);
        Assert.NotNull(withReader.Completeness);
    }

    private static ContextCapsule Assemble(SqliteIndexStore store, bool IncludeCompletenessDetail = false, bool supplyCompletenessReader = true)
        => new ContextAssembler
        {
            EdgeStore = store,
            DeclarationStore = store,
            BindingIncompletenessStore = supplyCompletenessReader ? store : null,
            SnapshotId = SnapshotId,
            SymbolId = SymbolId.Parse(AnchorId),
            Intent = ContextIntent.Inspect,
            Budget = 100_000,
            MaxHops = 3,
            IncludeGenerated = false,
            IncludeCompletenessDetail = IncludeCompletenessDetail,
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
                DocumentVersionId = "doc-v-completeness-dependency",
                FullSpan = new DeclarationSpan(0, Source.Length),
                SignatureSpan = new DeclarationSpan(0, Source.Length),
                BodySpan = new DeclarationSpan(0, Source.Length),
                NameSpan = new DeclarationSpan(18, 25),
                MetadataJson = """{"accessibility":"Public"}""",
            },
        ]);
        _store.SaveBindingIncompleteness(SnapshotId,
        [
            new BindingIncompletenessRecord("App", AnchorDocument, BindingIncompletenessReason.CompilerError, 5, "v1"),
            new BindingIncompletenessRecord("App", AnchorDocument, BindingIncompletenessReason.UnresolvedMetadata, 3, "v1"),
            new BindingIncompletenessRecord("App", "src/Other.cs", BindingIncompletenessReason.CompilerError, 9, "v1"),
        ]);
        return _store;
    }

    private void SeedFkReferences()
    {
        var lineStarts = ComputeLineStarts(Source);
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('ws-completeness-dependency', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'ws-completeness-dependency', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-completeness-dependency', 'src/Service.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, line_starts)
            VALUES ('doc-v-completeness-dependency', 'doc-completeness-dependency', 'hash', @content, @lineStarts);
            INSERT OR IGNORE INTO snapshot_documents (snapshot_id, document_version_id)
            VALUES (@sid, 'doc-v-completeness-dependency');
        ";
        cmd.Parameters.AddWithValue("@sid", SnapshotId);
        cmd.Parameters.AddWithValue("@content", Source);
        cmd.Parameters.AddWithValue("@lineStarts", JsonSerializer.Serialize(lineStarts));
        cmd.ExecuteNonQuery();
    }

    private static int[] ComputeLineStarts(byte[] content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == (byte)'\n')
                starts.Add(i + 1);
        }
        return starts.ToArray();
    }
}
