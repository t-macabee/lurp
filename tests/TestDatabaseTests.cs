using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class TestDatabaseTests
{
    private sealed class Probe : IntegrationTestBase
    {
    }

    private sealed class CacheProbe : IntegrationTestBase
    {
    }

    [Fact]
    public void IntegrationTestBase_Dispose_RemovesTestDir_AfterAPooledReadOnlyConnection()
    {
        var probe = new Probe();
        new MigrationRunner(probe.DbPath).RunMigrations();
        OpenAndClosePooledReadOnlyConnection(probe.DbPath);

        Assert.True(File.Exists(probe.DbPath));

        probe.Dispose();

        Assert.False(Directory.Exists(probe.TestDir));
    }

    [Fact]
    public void DeleteSolutionCache_RemovesTheCacheDirectoryOfASolution()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-testdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var solutionPath = Path.Combine(dir, "Test.slnx");
        var cacheDir = LurpCache.ResolveSolutionCacheDir(solutionPath);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "index.db"), "");

        try
        {
            TestDatabase.DeleteSolutionCache(solutionPath);

            Assert.False(Directory.Exists(cacheDir));
        }
        finally
        {
            TestDatabase.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void IntegrationTestBase_Dispose_RemovesTheSolutionCacheDir()
    {
        var probe = new CacheProbe();
        var cacheDir = LurpCache.ResolveSolutionCacheDir(probe.SolutionPath);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "index.db"), "");

        probe.Dispose();

        Assert.False(Directory.Exists(cacheDir));
        Assert.False(Directory.Exists(probe.TestDir));
    }

    [Fact]
    public void Delete_RemovesTheDatabaseAndItsWalAndShmFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lurp-testdb-{Guid.NewGuid():N}.db");
        using (var connection = TestDatabase.Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t (x INTEGER); INSERT INTO t VALUES (1);";
            command.ExecuteNonQuery();
        }

        File.WriteAllText(path + "-wal", "");
        File.WriteAllText(path + "-shm", "");

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + "-wal"));
        Assert.True(File.Exists(path + "-shm"));

        TestDatabase.Delete(path);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public void DeleteDirectory_RemovesTheDirectory_AfterAPooledReadOnlyConnection()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-testdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        new MigrationRunner(dbPath).RunMigrations();
        OpenAndClosePooledReadOnlyConnection(dbPath);

        Assert.True(File.Exists(dbPath));

        TestDatabase.DeleteDirectory(dir);

        Assert.False(Directory.Exists(dir));
    }

    private static void OpenAndClosePooledReadOnlyConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true
        };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
    }
}
