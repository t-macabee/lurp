using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     Writer-store lifecycle (B64): <c>Close()</c> releases the database file on
///     every open, not only the first one.
/// </summary>
public sealed class SqliteIndexStoreLifecycleTests : IntegrationTestBase
{
    [Fact]
    public void Close_ReleasesTheFile_AfterAReopen()
    {
        new MigrationRunner(DbPath).RunMigrations();
        var store = new SqliteIndexStore(DbPath);
        store.Open();
        store.Close();
        store.Open();
        store.Close();

        TestDatabase.Delete(DbPath);

        Assert.False(File.Exists(DbPath));
    }

    [Fact]
    public void Close_ReleasesTheFile_AfterOneOpen()
    {
        new MigrationRunner(DbPath).RunMigrations();
        var store = new SqliteIndexStore(DbPath);
        store.Open();
        store.Close();

        TestDatabase.Delete(DbPath);

        Assert.False(File.Exists(DbPath));
    }
}
