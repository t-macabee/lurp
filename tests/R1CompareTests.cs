using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

[Trait("Category", "Slow")]
public sealed class R1CompareTests : IntegrationTestBase
{
    internal const string ChangeEdgeSiteSql =
        "UPDATE edge_sites SET start_column = start_column + 1 WHERE rowid = (SELECT MIN(rowid) FROM edge_sites);";

    [Fact]
    public async Task R1Compare_FailsOnAnEdgeSiteDifference()
    {
        var result = await CompareIndexesAsync(changeSite: true);
        Assert.True(result.ExitCode == 1, $"Expected exit 1, got {result.ExitCode}.\n{result.Stdout}\n{result.Stderr}");
        Assert.Contains("edge_sites", result.Stderr);
    }

    [Fact]
    public async Task R1Compare_PassesOnEqualIndexes()
    {
        var result = await CompareIndexesAsync(changeSite: false);
        Assert.True(result.ExitCode == 0, $"{result.Stdout}\n{result.Stderr}");
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> CompareIndexesAsync(bool changeSite)
    {
        SnapshotAssertionsTests.CreateFixture(this);
        var fullPath = Path.Combine(TestDir, "full.db");
        await RunFullIndexAsync(DbPath);
        await RunFullIndexAsync(fullPath);
        if (changeSite)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = ChangeEdgeSiteSql;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var tool = ParityTestPaths.BuiltTool("scripts/r1-compare", "r1-compare");
        return TestProcess.Run("dotnet", [tool, DbPath, fullPath], TimeSpan.FromMinutes(1));
    }
}
