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
    public void MigrationList_CountIs28()
    {
        var versions = MigrationRunner.MigrationVersions;
        Assert.Equal(28, versions.Count);
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
        Assert.Equal(28, current);
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

    [Fact]
    public void ForwardMigration_FromV1Schema_PreservesSeededData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-e4-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                connection.Open();
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                new Migration_001_InitialSchema().Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (1, '2025-01-01T00:00:00.0000000Z', 'Migration_001_InitialSchema');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws-e4', '/repo/e4', '/repo/e4/E4.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('snap-e4', 'ws-e4', '2025-01-01T00:00:00.0000000Z', '10.0.100', 1, 1);

                    INSERT INTO documents (document_id, relative_path)
                    VALUES ('doc-e4', 'src/E4Project/E4.cs');

                    INSERT INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count)
                    VALUES ('dv-e4', 'doc-e4', 'abc123', X'010203', 'utf-8', 3);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            var finalVersion = new MigrationRunner(dbPath).GetCurrentSchemaVersion();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, finalVersion);

            using var store = new SqliteIndexStore(dbPath);
            store.Open();

            var snapshot = store.LoadLatestSnapshot("ws-e4");
            Assert.NotNull(snapshot);
            Assert.Equal("snap-e4", snapshot.SnapshotId);
            Assert.Equal("ws-e4", snapshot.WorkspaceId);
            Assert.Equal("/repo/e4", snapshot.GitRoot);

            var symbols = store.GetSymbolIdsInSnapshot("snap-e4");
            Assert.NotNull(symbols);
            Assert.Empty(symbols);

            var edges = store.GetEdges("snap-e4");
            Assert.NotNull(edges);
            Assert.Empty(edges);

            store.Close();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}