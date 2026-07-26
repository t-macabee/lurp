using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class T12DocumentVersionGarbageCollectionTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_t12_{Guid.NewGuid():N}.db");
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

    private static SnapshotRow CreateSnapshot(
        string snapshotId,
        DateTime createdAtUtc,
        string contentHash,
        string content,
        bool includeOldOnlyDocument = false)
    {
        var documents = new List<DocumentVersion>
        {
            new DocumentVersion(System.Text.Encoding.UTF8.GetBytes(content))
            {
                DocumentId = "doc1",
                FilePath = "src/Foo.cs",
                ContentHash = contentHash,
                Encoding = "utf-8",
                LineStart = "[0]",
                LineStarts = "[0]",
            },
        };

        if (includeOldOnlyDocument)
        {
            documents.Add(new DocumentVersion(System.Text.Encoding.UTF8.GetBytes("removed content"))
            {
                DocumentId = "doc-old-only",
                FilePath = "src/Removed.cs",
                ContentHash = "hash-old-only",
                Encoding = "utf-8",
                LineStart = "[0]",
                LineStarts = "[0]",
            });
        }

        return new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = "workspace:///t12",
            GitRoot = "/t12",
            SolutionPath = "/t12/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = createdAtUtc,
            Documents = documents,
        };
    }

    private static void SaveCompleteSnapshot(
        SqliteIndexStore store,
        string snapshotId,
        DateTime createdAtUtc,
        string contentHash,
        string content,
        bool includeOldOnlyDocument = false)
    {
        store.SaveSnapshot(CreateSnapshot(
            snapshotId, createdAtUtc, contentHash, content, includeOldOnlyDocument));
        store.MarkSnapshotComplete(snapshotId);
    }

    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    [Fact]
    public void PruneOldSnapshots_RemovesUnreferencedVersions_AndRetainedSnapshotRemainsReadable()
    {
        var store = CreateStore();
        var baseTime = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        SaveCompleteSnapshot(
            store, "snap-t12-old", baseTime, "hash-old", "old content", includeOldOnlyDocument: true);
        SaveCompleteSnapshot(store, "snap-t12-middle", baseTime.AddMinutes(1), "hash-middle", "middle content");
        SaveCompleteSnapshot(store, "snap-t12-retained", baseTime.AddMinutes(2), "hash-retained", "retained content");

        store.PruneOldSnapshots(keep: 2);

        Assert.Equal("retained content", store.GetSource("src/Foo.cs", "snap-t12-retained"));
        Assert.Null(store.GetSource("src/Foo.cs", "snap-t12-old"));
        Assert.Equal(0, Scalar<long>(
            "SELECT COUNT(*) FROM document_versions WHERE document_version_id = 'doc1:hash-old';"));
        Assert.Equal(0, Scalar<long>(
            "SELECT COUNT(*) FROM documents WHERE document_id = 'doc-old-only';"));
        Assert.Equal(0, Scalar<long>(
            "SELECT COUNT(*) FROM document_versions WHERE document_version_id = 'doc-old-only:hash-old-only';"));
        Assert.Equal(1, Scalar<long>(
            "SELECT COUNT(*) FROM document_versions WHERE document_version_id = 'doc1:hash-middle';"));
        Assert.Equal(1, Scalar<long>(
            "SELECT COUNT(*) FROM document_versions WHERE document_version_id = 'doc1:hash-retained';"));
        Assert.Equal(1, Scalar<long>("SELECT COUNT(*) FROM documents WHERE document_id = 'doc1';"));
    }

    [Fact]
    public void PruneOldSnapshots_RepairsDanglingLastChangedSnapshotId()
    {
        var store = CreateStore();
        var baseTime = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);

        SaveCompleteSnapshot(store, "snap-t12-pruned", baseTime, "hash-old", "old content");
        SaveCompleteSnapshot(store, "snap-t12-kept", baseTime.AddMinutes(1), "hash-kept", "kept content");

        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE documents
                SET last_changed_snapshot_id = 'snap-t12-pruned'
                WHERE document_id = 'doc1';
            ";
            command.ExecuteNonQuery();
        }

        store.PruneOldSnapshots(keep: 1);

        Assert.Equal("snap-t12-kept", Scalar<string>(
            "SELECT last_changed_snapshot_id FROM documents WHERE document_id = 'doc1';"));
        Assert.Equal("kept content", store.GetSource("src/Foo.cs", "snap-t12-kept"));
    }
}
