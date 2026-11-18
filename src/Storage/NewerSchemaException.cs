namespace Lurp.Storage;

/// <summary>
///     Thrown by <see cref="MigrationRunner.RunMigrations" /> before opening a write connection
///     when an existing database carries a schema version newer than <see cref="MigrationRunner.LatestVersion" />.
/// </summary>
internal sealed class NewerSchemaException(string dbPath, int actualVersion, int expectedVersion)
    : InvalidOperationException(SchemaVersionMessages.Newer(dbPath, actualVersion, expectedVersion))
{
    public string DbPath { get; } = dbPath;
    public int ActualVersion { get; } = actualVersion;
    public int ExpectedVersion { get; } = expectedVersion;
}
