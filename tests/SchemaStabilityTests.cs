using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class SchemaStabilityTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-schema-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void MigrationListAndSchemaConstant_AreConsistent()
    {
        var versions = MigrationRunner.MigrationVersions;
        Assert.NotEmpty(versions);
        Assert.Equal(VersionConstants.DatabaseSchemaVersion, versions.Max());
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public void CheckedInPriorV22Fixture_UpgradesToCurrentSchema()
    {
        var fixture = Path.Combine(LocateRepositoryRoot(), "tests", "fixtures", "schema", "prior-v22.sql");
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(fixture);
            command.ExecuteNonQuery();
        }

        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());
        using var upgraded = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        upgraded.Open();
        using var columns = upgraded.CreateCommand();
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('snapshots') WHERE name IN ('failure_reason_code', 'failure_message');";
        Assert.Equal(2L, (long)columns.ExecuteScalar()!);
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('edges') WHERE name = 'receiver_type_constraints_json';";
        Assert.Equal(1L, (long)columns.ExecuteScalar()!);
        columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('annotations') WHERE name = 'document_path';";
        Assert.Equal(1L, (long)columns.ExecuteScalar()!);
    }

    [Fact]
    public void UnknownPersistedSymbolKind_DeserializesAsUnknown()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO workspaces (workspace_id, git_root, solution_path) VALUES ('ws', '.', 'Lurp.slnx');
                INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, status) VALUES ('snap', 'ws', '2026-08-01T00:00:00Z', 'complete');
                INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind) VALUES ('T:Future|asm', 'T:Future', 'asm', 'FutureKind');
                INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn) VALUES ('snap', 'T:Future|asm', 'Future');";
            command.ExecuteNonQuery();
        }

        var info = store.GetSymbolInfo("T:Future|asm", "snap");
        Assert.NotNull(info);
        Assert.Equal(IndexedSymbolKind.Unknown, info.Kind);
    }

    private static string LocateRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lurp.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
