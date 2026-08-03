using Microsoft.Data.Sqlite;
using Lurp.Workspace;

namespace Lurp.Storage.Tests;

// PR-5: freshness stamp on every read. WorkspaceFreshness.CheckFreshnessCheap
// must detect staleness without loading a Roslyn workspace (that cost is what
// made the pre-existing WorkspaceFreshness.CheckFreshness only usable behind
// `status --solution=`). These tests pin the stat-only default tier, the
// hash-escalation tier that resolves a touch-without-content-change false
// positive, and the --freshness=off bypass.
public sealed class FreshnessCheapCheckTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_freshness_{Guid.NewGuid():N}.db");
    private readonly string _gitRoot = Path.Combine(
        Path.GetTempPath(), $"indexer_freshness_repo_{Guid.NewGuid():N}");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        if (Directory.Exists(_gitRoot))
            Directory.Delete(_gitRoot, recursive: true);
    }

    private SqliteIndexStore CreateStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        return _store;
    }

    private static void SaveSnapshot(SqliteIndexStore store, string snapshotId, string gitRoot, DateTime builtAtUtc)
    {
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = "ws-" + snapshotId,
            GitRoot = gitRoot,
            SolutionPath = Path.Combine(gitRoot, "test.sln"),
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = builtAtUtc,
            Documents = [],
        });
    }

    private static void SeedDocumentVersion(string dbPath, string documentId, string relativePath, string versionId, string contentHash)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT OR IGNORE INTO documents (document_id, relative_path, last_changed_snapshot_id)
            VALUES (@documentId, @relativePath, NULL);
        ";
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@relativePath", relativePath);
        command.ExecuteNonQuery();

        command.CommandText = @"
            INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
            VALUES (@versionId, @documentId, @contentHash);
        ";
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@versionId", versionId);
        command.Parameters.AddWithValue("@documentId", documentId);
        command.Parameters.AddWithValue("@contentHash", contentHash);
        command.ExecuteNonQuery();
    }

    private string WriteSourceFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_gitRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    [Fact]
    public void CheckFreshnessCheap_UntouchedDocument_ReportsFresh()
    {
        var store = CreateStore();
        var builtAt = DateTime.UtcNow.AddMinutes(-10);
        SaveSnapshot(store, "snap-fresh", _gitRoot, builtAt);

        var content = "class Foo {}";
        WriteSourceFile("Foo.cs", content);
        var hash = DocumentVersionId.Compute(new DocumentId("Foo.cs"), System.Text.Encoding.UTF8.GetBytes(content)).Hash;
        var versionId = $"doc-foo:{hash}";
        SeedDocumentVersion(_dbPath, "doc-foo", "Foo.cs", versionId, hash);
        store.SaveSnapshotDocuments("snap-fresh", [("doc-foo", versionId)]);

        // The file's mtime is "now" (after builtAt), so a naive stat check
        // would call this stale. Back-date the write time to before builtAt
        // to isolate the "genuinely untouched" case.
        File.SetLastWriteTimeUtc(Path.Combine(_gitRoot, "Foo.cs"), builtAt.AddMinutes(-1));

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, "snap-fresh", FreshnessMode.Auto);

        Assert.Equal("fresh", stamp.State);
        Assert.Equal("stat", stamp.Method);
        Assert.Equal(0, stamp.ChangedDocumentCount);
    }

    [Fact]
    public void CheckFreshnessCheap_TouchedDocument_AutoModeReportsStaleWithoutHashing()
    {
        var store = CreateStore();
        var builtAt = DateTime.UtcNow.AddMinutes(-10);
        SaveSnapshot(store, "snap-touched", _gitRoot, builtAt);

        var content = "class Foo {}";
        WriteSourceFile("Foo.cs", content);
        var hash = DocumentVersionId.Compute(new DocumentId("Foo.cs"), System.Text.Encoding.UTF8.GetBytes(content)).Hash;
        var versionId = $"doc-foo:{hash}";
        SeedDocumentVersion(_dbPath, "doc-foo", "Foo.cs", versionId, hash);
        store.SaveSnapshotDocuments("snap-touched", [("doc-foo", versionId)]);
        // WriteSourceFile already left the file's mtime "now" (after builtAt) :
        // same bytes, but the stat-only tier cannot tell touched from changed.

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, "snap-touched", FreshnessMode.Auto);

        Assert.Equal("stale", stamp.State);
        Assert.Equal("stat", stamp.Method);
        Assert.Equal(1, stamp.ChangedDocumentCount);
        Assert.Contains("Foo.cs", stamp.ChangedDocumentsSample);
    }

    [Fact]
    public void CheckFreshnessCheap_TouchedButUnchangedDocument_HashModeResolvesToFresh()
    {
        var store = CreateStore();
        var builtAt = DateTime.UtcNow.AddMinutes(-10);
        SaveSnapshot(store, "snap-hash", _gitRoot, builtAt);

        var content = "class Foo {}";
        WriteSourceFile("Foo.cs", content);
        var hash = DocumentVersionId.Compute(new DocumentId("Foo.cs"), System.Text.Encoding.UTF8.GetBytes(content)).Hash;
        var versionId = $"doc-foo:{hash}";
        SeedDocumentVersion(_dbPath, "doc-foo", "Foo.cs", versionId, hash);
        store.SaveSnapshotDocuments("snap-hash", [("doc-foo", versionId)]);
        // Same content, newer mtime than builtAt (touch without edit).

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, "snap-hash", FreshnessMode.Hash);

        Assert.Equal("fresh", stamp.State);
        Assert.Equal("stat+hash", stamp.Method);
        Assert.Equal(0, stamp.ChangedDocumentCount);
    }

    [Fact]
    public void CheckFreshnessCheap_TouchedAndActuallyChangedDocument_HashModeReportsStale()
    {
        var store = CreateStore();
        var builtAt = DateTime.UtcNow.AddMinutes(-10);
        SaveSnapshot(store, "snap-realchange", _gitRoot, builtAt);

        var originalContent = "class Foo {}";
        WriteSourceFile("Foo.cs", originalContent);
        var storedHash = DocumentVersionId.Compute(new DocumentId("Foo.cs"), System.Text.Encoding.UTF8.GetBytes(originalContent)).Hash;
        var versionId = $"doc-foo:{storedHash}";
        SeedDocumentVersion(_dbPath, "doc-foo", "Foo.cs", versionId, storedHash);
        store.SaveSnapshotDocuments("snap-realchange", [("doc-foo", versionId)]);

        // Actually edit the file after the snapshot was built.
        WriteSourceFile("Foo.cs", "class Foo { void Bar() {} }");

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, "snap-realchange", FreshnessMode.Hash);

        Assert.Equal("stale", stamp.State);
        Assert.Equal("stat+hash", stamp.Method);
        Assert.Equal(1, stamp.ChangedDocumentCount);
    }

    [Fact]
    public void CheckFreshnessCheap_RemovedDocument_ReportsStale()
    {
        var store = CreateStore();
        var builtAt = DateTime.UtcNow.AddMinutes(-10);
        SaveSnapshot(store, "snap-removed", _gitRoot, builtAt);

        SeedDocumentVersion(_dbPath, "doc-gone", "Gone.cs", "doc-gone:v1", "irrelevant-hash");
        store.SaveSnapshotDocuments("snap-removed", [("doc-gone", "doc-gone:v1")]);
        // Deliberately never write Gone.cs to disk.

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, "snap-removed", FreshnessMode.Auto);

        Assert.Equal("stale", stamp.State);
        Assert.Equal(1, stamp.ChangedDocumentCount);
        Assert.Contains("Gone.cs", stamp.ChangedDocumentsSample);
    }

    [Fact]
    public void CheckFreshnessCheap_OffMode_SkipsTheScanEntirely()
    {
        var store = CreateStore();
        var builtAt = DateTime.UtcNow.AddMinutes(-10);
        SaveSnapshot(store, "snap-off", _gitRoot, builtAt);

        SeedDocumentVersion(_dbPath, "doc-gone", "Gone.cs", "doc-gone:v1", "irrelevant-hash");
        store.SaveSnapshotDocuments("snap-off", [("doc-gone", "doc-gone:v1")]);

        var stamp = WorkspaceFreshness.CheckFreshnessCheap(store, "snap-off", FreshnessMode.Off);

        Assert.Equal("unknown", stamp.State);
        Assert.Equal("skipped", stamp.Method);
        Assert.Equal(0, stamp.ChangedDocumentCount);
    }
}
