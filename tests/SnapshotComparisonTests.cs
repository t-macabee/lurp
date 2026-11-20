using Lurp.Parity.Shared;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

[Trait("Category", "Slow")]
public sealed class SnapshotComparisonTests : IntegrationTestBase
{
    [Fact]
    public async Task Compare_ReportsATableThatIsNeitherComparedNorExcluded()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();

        foreach (var path in new[] { DbPath, copy })
        {
            using var connection = TestDatabase.Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE extra_facts (x INTEGER)";
            command.ExecuteNonQuery();
        }

        Assert.Equal(["table extra_facts is neither compared nor excluded"],
            SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
    }

    [Fact]
    public async Task Compare_ReportsAStoredSymbolIdentityDifference()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        using (var connection = TestDatabase.Open(copy))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE symbols SET doc_comment_id = 'T:Wrong';
                """;
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        var difference = Assert.Single(SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
        Assert.StartsWith("symbols:", difference, StringComparison.Ordinal);
    }

    private async Task<(string Snapshot, string Copy)> IndexAndCopyAsync()
    {
        SnapshotAssertionsTests.CreateFixture(this);
        var snapshot = await RunFullIndexAsync(DbPath);
        var copy = Path.Combine(TestDir, "copy.db");
        SqliteConnection.ClearAllPools();
        File.Copy(DbPath, copy);
        return (snapshot, copy);
    }
}
