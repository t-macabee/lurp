using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     Row-level tests for the two <see cref="SemanticDiffStore" /> reads that append a
///     <c>WHERE</c> clause to the shared select. The shared select must end in whitespace
///     before the appended clause (6635f2c); without it SQLite parses
///     <c>semantic_changesWHERE</c> and every scoped diff read fails.
/// </summary>
public sealed class SemanticDiffStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-semantic-diff-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.Delete(_dbPath);
    }

    [Fact]
    public void GetSemanticChanges_ReturnsOnlyTheRequestedFromToPairInOrder()
    {
        using var store = OpenSeededStore();

        var changes = store.GetSemanticChanges("s1", "s2");

        Assert.Equal(["c1", "c2"], changes.Select(c => c.ChangeId));
        Assert.All(changes, change =>
        {
            Assert.Equal("s1", change.FromSnapshotId);
            Assert.Equal("s2", change.ToSnapshotId);
        });
        Assert.Equal("detail-1", changes[0].DetailJson);
    }

    [Fact]
    public void GetSemanticChangesToSnapshot_ReturnsEveryFromSnapshotForThatTargetInOrder()
    {
        using var store = OpenSeededStore();

        var changes = store.GetSemanticChangesToSnapshot("s2");

        Assert.Equal(["c1", "c2", "c4"], changes.Select(c => c.ChangeId));
        Assert.All(changes, change => Assert.Equal("s2", change.ToSnapshotId));
        Assert.Empty(store.GetSemanticChangesToSnapshot("missing"));
    }

    private SqliteIndexStore OpenSeededStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        store.SaveSemanticChanges("s1", "s2",
        [
            new SemanticChange
            {
                ChangeId = "c2", FromSnapshotId = "s1", ToSnapshotId = "s2",
                ChangeType = "body_only_changed", SymbolId = "M:Probe.Bar()",
                DetailJson = "detail-2", CreatedAtUtc = createdAt.AddMinutes(1)
            },
            new SemanticChange
            {
                ChangeId = "c1", FromSnapshotId = "s1", ToSnapshotId = "s2",
                ChangeType = "signature_changed", SymbolId = "M:Probe.Foo()",
                DetailJson = "detail-1", CreatedAtUtc = createdAt
            }
        ]);
        store.SaveSemanticChanges("s0", "s2",
        [
            new SemanticChange
            {
                ChangeId = "c4", FromSnapshotId = "s0", ToSnapshotId = "s2",
                ChangeType = "edge_added", SymbolId = "M:Probe.Baz()",
                DetailJson = null, CreatedAtUtc = createdAt.AddMinutes(2)
            }
        ]);
        store.SaveSemanticChanges("s2", "s3",
        [
            new SemanticChange
            {
                ChangeId = "c3", FromSnapshotId = "s2", ToSnapshotId = "s3",
                ChangeType = "symbol_added", SymbolId = "M:Probe.Quux()",
                DetailJson = "detail-3", CreatedAtUtc = createdAt.AddMinutes(3)
            }
        ]);

        return store;
    }
}
