using Lurp.Storage;
using Lurp.Storage.Migrations;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class SchemaMigrationRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-schema-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void MigrationList_CountIs27()
    {
        var versions = MigrationRunner.MigrationVersions;
        Assert.Equal(27, versions.Count);
    }

    [Fact]
    public void MigrationList_HighestVersionMatches_DatabaseSchemaVersion()
    {
        var versions = MigrationRunner.MigrationVersions;
        Assert.NotEmpty(versions);
        Assert.Equal(VersionConstants.DatabaseSchemaVersion, versions.Max());
    }

    [Fact]
    public void MigrationList_AllVersionsAreUnique()
    {
        var versions = MigrationRunner.MigrationVersions;
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public void RoundTrip_AllMigrations_ProducesCurrentSchema()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        var current = runner.GetCurrentSchemaVersion();
        Assert.Equal(VersionConstants.DatabaseSchemaVersion, current);
        Assert.Equal(27, current);
    }

    [Fact]
    public void RoundTrip_SnapshotTable_HasStatusColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('snapshots') WHERE name = 'status';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_EdgesTable_HasReceiverTypeConstraints()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('edges') WHERE name = 'receiver_type_constraints_json';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_AnnotationsTable_HasDocumentPath()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('annotations') WHERE name = 'document_path';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_ProjectsTable_HasCompilationInputColumns()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name IN ('metadata_reference_identities', 'compilation_options_fingerprint');";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }
}
