using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Storage.Tests;

/// <summary>
/// Contract tests proving that every consumer treats the canonical TestedBy edge
/// direction (production source -> test target) consistently.
/// </summary>
public sealed class TestedByContractTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_testedby_contract_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SqliteIndexStore CreateStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open(_dbPath);
        _store.RunMigrations();
        return _store;
    }

    private void SeedFkReferences(string snapshotId)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
            VALUES ('test-ws', '/fake/root', 'test.sln');
            INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
            VALUES (@sid, 'test-ws', '2026-01-01T00:00:00Z');
            INSERT OR IGNORE INTO documents (document_id, relative_path)
            VALUES ('doc-tc', 'test.cs');
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc-v-tc', 'doc-tc', 'hash');
        ";
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.ExecuteNonQuery();
    }

    private static SymbolDeclaration MakeDecl(string symbolId, string? metadataJson = null)
    {
        var sid = SymbolId.Parse(symbolId);
        return new SymbolDeclaration
        {
            SymbolId = sid,
            Kind = IndexedSymbolKind.Method,
            DocumentVersionId = "doc-v-tc",
            FullSpan = new DeclarationSpan(null, null),
            SignatureSpan = new DeclarationSpan(null, null),
            BodySpan = new DeclarationSpan(null, null),
            NameSpan = new DeclarationSpan(null, null),
            MetadataJson = metadataJson ?? """{"accessibility":"Public"}""",
        };
    }

    [Fact]
    public void TestAdapter_ProductionToTestEdge_IsConsumedConsistently()
    {
        const string snapId = "snap-tc-001";
        var store = CreateStore();
        SeedFkReferences(snapId);

        store.SaveDeclarations(snapId,
        [
            MakeDecl("M:A|prod"),
            MakeDecl("M:T|test", "{}"),
        ]);

        // Persist a single TestedBy edge: production A -> test T
        store.SaveEdges(snapId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "M:A|prod",
                TargetSymbolId = "M:T|test",
                Kind = EdgeKind.TestedBy.ToString(),
                Provenance = "framework_derived",
                SnapshotId = snapId,
                ExtractorVersion = "test-v3",
            },
        ]);

        // --- AuditEngine: production symbol (source) must be covered ---
        var auditEngine = new AuditEngine(store, snapId);
        var auditReport = auditEngine.RunAudit(new AuditOptions(["untested-surface"]));

        Assert.DoesNotContain(
            auditReport.Findings,
            f => f.Check == "untested-surface" && f.SymbolId == "M:A|prod");

        // --- SimulationEngine: removing production reports target test ---
        var simEngine = new SimulationEngine(store, store, snapId);
        var simReport = simEngine.SimulateRemove("M:A|prod");

        Assert.Contains(
            simReport.Items,
            i => i.EdgeKind == "TestedBy" && i.SymbolId == "M:T|test");

        // --- UncertaintyDetector: outgoing edges suggest target test ---
        var symbolId = SymbolId.Parse("M:A|prod");
        var detector = new UncertaintyDetector(store, store, snapId, symbolId, false);
        var capsule = new ContextCapsule(
            new CapsuleAnchor("M:A|prod", "MyApp.A", "Method", string.Empty));

        detector.Detect(capsule);

        Assert.Contains(
            capsule.SuggestedVerification,
            v => v.TestId == "M:T|test");
    }

    [Fact]
    public void ContextAssembler_UpstreamProductionWithTestedByEdge_ReturnsTargetTest()
    {
        const string snapId = "snap-tc-002";
        var store = CreateStore();
        SeedFkReferences(snapId);

        store.SaveDeclarations(snapId,
        [
            MakeDecl("M:A|prod"),
            MakeDecl("M:B|prod"),
            MakeDecl("M:T|test", "{}"),
        ]);

        // B calls A; B (upstream caller) has TestedBy edge to test T
        store.SaveEdges(snapId,
        [
            new EdgeRecord
            {
                SourceSymbolId = "M:B|prod",
                TargetSymbolId = "M:A|prod",
                Kind = EdgeKind.Calls.ToString(),
                Provenance = "compiler_proved",
                SnapshotId = snapId,
                ExtractorVersion = "v1",
            },
            new EdgeRecord
            {
                SourceSymbolId = "M:B|prod",
                TargetSymbolId = "M:T|test",
                Kind = EdgeKind.TestedBy.ToString(),
                Provenance = "framework_derived",
                SnapshotId = snapId,
                ExtractorVersion = "test-v3",
            },
        ]);

        // Assemble context for A (downstream symbol whose caller B has tests)
        var symbolId = SymbolId.Parse("M:A|prod");
        var assembler = new ContextAssembler
        {
            EdgeStore = store,
            DeclarationStore = store,
            SnapshotId = snapId,
            SymbolId = symbolId,
            Intent = ContextIntent.Inspect,
            Budget = 100_000,
            MaxHops = 3,
            IncludeGenerated = false,
        };

        var capsule = assembler.Assemble();

        // The relevant-tests tier should contain test T (found via B's outgoing TestedBy)
        Assert.Contains(
            capsule.RelevantTests,
            t => t.SymbolId == "M:T|test");

        // The relevant-tests tier must NOT contain the upstream production caller B
        Assert.DoesNotContain(
            capsule.RelevantTests,
            t => t.SymbolId == "M:B|prod");
    }

    [Fact]
    public void MethodAnchor_TypeLevelTestedBy_EmitsExecutableVerificationStep()
    {
        const string snapId = "snap-tc-method";
        const string methodId = "M:MyApp.Service.Execute|prod";
        const string typeId = "T:MyApp.Service|prod";
        const string testId = "M:MyApp.Tests.ServiceTests.Execute_Works|test";
        var store = CreateStore();
        SeedFkReferences(snapId);
        store.SaveDeclarations(snapId, [MakeDecl(methodId), MakeDecl(typeId), MakeDecl(testId)]);
        store.SaveEdges(snapId,
        [
            new EdgeRecord
            {
                SourceSymbolId = typeId,
                TargetSymbolId = testId,
                Kind = EdgeKind.TestedBy.ToString(),
                Provenance = "framework_derived",
                SnapshotId = snapId,
                ExtractorVersion = "test-v3",
            },
        ]);

        var capsule = new ContextCapsule(new CapsuleAnchor(methodId, "MyApp.Service.Execute", "Method", ""));
        new UncertaintyDetector(store, store, snapId, SymbolId.Parse(methodId), false).Detect(capsule);

        var step = Assert.Single(capsule.SuggestedVerification);
        Assert.Equal(testId, step.TestId);
        Assert.False(string.IsNullOrWhiteSpace(step.Command));
        Assert.Contains("dotnet test", step.Command, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName=", step.Command, StringComparison.Ordinal);
    }
}
