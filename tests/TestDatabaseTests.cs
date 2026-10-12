using Lurp.Storage;

namespace Lurp.Tests;

public sealed class TestDatabaseTests
{
    private sealed class Probe : IntegrationTestBase
    {
    }

    [Fact]
    public void IntegrationTestBase_Dispose_RemovesTestDir_AfterAPooledReadOnlyStore()
    {
        var probe = new Probe();
        new MigrationRunner(probe.DbPath).RunMigrations();
        using (var store = new SqliteIndexStore(probe.DbPath))
        {
            store.OpenReadOnly();
            store.Close();
        }

        Assert.True(File.Exists(probe.DbPath));

        probe.Dispose();

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
    public void DeleteDirectory_RemovesTheDirectory_AfterAPooledReadOnlyStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-testdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        new MigrationRunner(dbPath).RunMigrations();
        using (var store = new SqliteIndexStore(dbPath))
        {
            store.OpenReadOnly();
            store.Close();
        }

        Assert.True(File.Exists(dbPath));

        TestDatabase.DeleteDirectory(dir);

        Assert.False(Directory.Exists(dir));
    }
}
