using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

// PR-4: unambiguous metric names in console/JSON output. These tests pin the
// snapshot-scoped "in_snapshot" counters (CountSymbolsInSnapshot, CountEdges,
// CountDiagnostics) that IndexRunner/IncrementalIndexer now report alongside
// the "this run" extraction totals, so the two numbers can never again be
// confused with each other.
public sealed class SnapshotFactCountTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_factcount_{Guid.NewGuid():N}.db");
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
        _store.Open();
        _store.RunMigrations();
        return _store;
    }

    private static void SaveSnapshot(SqliteIndexStore store, string snapshotId, string workspaceId)
    {
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/repo",
            SolutionPath = "/repo/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = [],
        });
    }

    private static EdgeRecord MakeEdge(string source, string target, string kind = "Calls") => new()
    {
        SourceSymbolId = source,
        TargetSymbolId = target,
        Kind = kind,
        Provenance = "compiler",
        ExtractorVersion = "1",
    };

    private static DiagnosticRecord MakeDiagnostic(string id) => new()
    {
        ProjectName = "TestProject",
        Severity = "Warning",
        Id = id,
        Message = "test diagnostic",
    };

    [Fact]
    public void CountEdges_ScopesToRequestedSnapshot()
    {
        var store = CreateStore();
        SaveSnapshot(store, "snap-edges-1", "ws-edges-1");
        SaveSnapshot(store, "snap-edges-2", "ws-edges-1");

        store.SaveEdges("snap-edges-1", [MakeEdge("A", "B"), MakeEdge("B", "C")]);
        store.SaveEdges("snap-edges-2", [MakeEdge("A", "B")]);

        Assert.Equal(2, store.CountEdges("snap-edges-1"));
        Assert.Equal(1, store.CountEdges("snap-edges-2"));
    }

    [Fact]
    public void CountDiagnostics_ScopesToRequestedSnapshot()
    {
        var store = CreateStore();
        SaveSnapshot(store, "snap-diag-1", "ws-diag-1");
        SaveSnapshot(store, "snap-diag-2", "ws-diag-1");

        store.SaveDiagnostics("snap-diag-1", [MakeDiagnostic("CS0001"), MakeDiagnostic("CS0002")]);
        store.SaveDiagnostics("snap-diag-2", [MakeDiagnostic("CS0001")]);

        Assert.Equal(2, store.CountDiagnostics("snap-diag-1"));
        Assert.Equal(1, store.CountDiagnostics("snap-diag-2"));
    }

    private static SymbolDeclaration MakeSymbol(string docCommentId) => MigrationRunnerTests.MakeDecl(
        docCommentId, "asm1", IndexedSymbolKind.NamedType, "doc:v1",
        0, 100, 10, 50, 30, 90, 15, 21);

    private void SeedDocumentVersion(string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO documents (document_id, relative_path, last_changed_snapshot_id)
            VALUES ('doc', 'test.cs', @snapshotId);
        ";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.ExecuteNonQuery();

        command.CommandText = @"
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES ('doc:v1', 'doc', 'hash1');
        ";
        command.ExecuteNonQuery();
    }

    [Fact]
    public void CountSymbolsInSnapshot_ScopesToRequestedSnapshot()
    {
        var store = CreateStore();
        SaveSnapshot(store, "snap-sym-1", "ws-sym-1");
        SaveSnapshot(store, "snap-sym-2", "ws-sym-1");
        SeedDocumentVersion("snap-sym-1");

        store.SaveDeclarations("snap-sym-1", [MakeSymbol("T:Ns.A"), MakeSymbol("T:Ns.B")]);
        store.SaveDeclarations("snap-sym-2", [MakeSymbol("T:Ns.A")]);

        Assert.Equal(2, store.CountSymbolsInSnapshot("snap-sym-1"));
        Assert.Equal(1, store.CountSymbolsInSnapshot("snap-sym-2"));
    }

    [Fact]
    public void EdgesInSnapshot_CanDifferFromRawExtractedCount_AfterOrphanCleanup()
    {
        // Mirrors D7: the "this run" total (raw extracted/deduped edges) and the
        // "in snapshot" total (after orphan-edge deletion) are legitimately
        // different numbers and must never share one ambiguous field name.
        var store = CreateStore();
        SaveSnapshot(store, "snap-orphan-1", "ws-orphan-1");
        SeedDocumentVersion("snap-orphan-1");
        store.SaveDeclarations("snap-orphan-1", [MakeSymbol("T:Ns.A")]);
        var symbolId = MakeSymbol("T:Ns.A").SymbolId.Value;

        var extractedThisRun = new[] { MakeEdge(symbolId, symbolId), MakeEdge(symbolId, "sym:GoneNow") };
        store.SaveEdges("snap-orphan-1", extractedThisRun);
        Assert.Equal(extractedThisRun.Length, store.CountEdges("snap-orphan-1"));

        store.DeleteOrphanEdges("snap-orphan-1");

        Assert.Equal(1, store.CountEdges("snap-orphan-1"));
        Assert.NotEqual(extractedThisRun.Length, store.CountEdges("snap-orphan-1"));
    }
}
