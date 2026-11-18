using Lurp.Storage;

namespace Lurp.Tests;

public sealed class AnnotationUniquenessTests : IDisposable
{
    private const string WorkspaceId = "w1";
    private const string SnapshotId = "s1";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-ann-uniq-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.Delete(_dbPath);
    }

    private SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        store.SaveWorkspace(WorkspaceId, "gitroot", "solution.sln");
        store.SaveSnapshot(new SnapshotRow
        {
            SnapshotId = SnapshotId,
            WorkspaceId = WorkspaceId,
            GitRoot = "gitroot",
            SolutionPath = "solution.sln",
            CreatedAtUtc = DateTime.UtcNow
        });
        return store;
    }

    [Fact]
    public void SaveAnnotations_SameExtractorFactTwice_StoresOneRow()
    {
        using var store = OpenStore();
        var record = new AnnotationRecord("S", "unmodeled_mediatr_pattern", "IStreamRequestHandler", "src/App/H.cs");

        store.SaveAnnotations(SnapshotId, [record]);
        store.SaveAnnotations(SnapshotId, [record]);
        store.SaveAnnotations(SnapshotId, [record, record]);

        var rows = store.GetAnnotations(SnapshotId);
        var single = Assert.Single(rows);
        Assert.Equal("S", single.SymbolId);
        Assert.Equal("unmodeled_mediatr_pattern", single.Kind);
        Assert.Equal("IStreamRequestHandler", single.Value);
        Assert.Equal("src/App/H.cs", single.DocumentPath);
    }

    [Fact]
    public void SaveAnnotations_UserAnnotationWithoutPathTwice_KeepsBothRows()
    {
        using var store = OpenStore();
        var record = new AnnotationRecord("S", "note", "x", null);

        store.SaveAnnotations(SnapshotId, [record]);
        store.SaveAnnotations(SnapshotId, [record]);

        var rows = store.GetAnnotations(SnapshotId);
        Assert.Equal(2, rows.Count);
        Assert.NotEqual(rows[0].AnnotationId, rows[1].AnnotationId);
    }
}
