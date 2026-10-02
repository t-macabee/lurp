using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace Lurp.Tests;

public sealed class SchemaVersionGuardTests : IDisposable
{
    private const int StaleVersion = 29;

    private readonly string _root;
    private readonly string _outputDir;
    private readonly string _dbPath;

    public SchemaVersionGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lurp-schema-guard-{Guid.NewGuid():N}");
        _outputDir = Path.Combine(_root, "output");
        Directory.CreateDirectory(_outputDir);
        _dbPath = Path.Combine(_outputDir, "index.db");

        // Build a current-schema database, copy it, and downgrade the copy's marker to v29.
        var sourceDb = Path.Combine(_root, "source", "index.db");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDb)!);
        new MigrationRunner(sourceDb).RunMigrations();
        File.Copy(sourceDb, _dbPath);

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM schema_metadata WHERE version > {StaleVersion};";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Message(int actualVersion)
    {
        var expected = VersionConstants.DatabaseSchemaVersion;
        return actualVersion < expected
            ? $"ERROR: Index database at {_dbPath} uses schema v{actualVersion}; this Lurp needs v{expected}. Run 'lurp --mode=index' to update it."
            : $"ERROR: Index database at {_dbPath} uses schema v{actualVersion}, newer than this Lurp (v{expected}). Update Lurp.";
    }

    [Fact]
    public void CliReadMode_OnStaleSchema_FailsWithMessage_AndDoesNotMigrate()
    {
        var ex = Assert.Throws<CliExitException>(() =>
            GrepHandler.Run([$"--output-dir={_outputDir}", "--query=Foo"]));

        Assert.Equal(Message(StaleVersion), ex.Message);
        Assert.Equal(StaleVersion, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());
    }

    [Fact]
    public void McpSession_OnStaleSchema_FailsWithMessage_AndDoesNotMigrate()
    {
        var ex = Assert.Throws<CliExitException>(() =>
            McpSessionContext.Create([$"--output-dir={_outputDir}"]));

        Assert.Equal(Message(StaleVersion), ex.Message);
        Assert.Equal(StaleVersion, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());
    }

    [Fact]
    public async Task Status_OnStaleSchema_ReportsMismatch_AndDoesNotMigrate()
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(stdout);
            await StatusHandler.Run([$"--output-dir={_outputDir}", "--json"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Equal(StaleVersion, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(StaleVersion, doc.RootElement.GetProperty("schema_version").GetInt32());
        Assert.True(doc.RootElement.GetProperty("schema_version_mismatch").GetBoolean());
        Assert.Contains("--mode=index", doc.RootElement.GetProperty("schema_version_note").GetString()!);
    }

    [Fact]
    public void CliReadMode_OnNewerSchema_FailsWithNewerMessage_AndDoesNotChangeIt()
    {
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_metadata (version, applied_at_utc, migration_id) VALUES (31, '2030-01-01T00:00:00.0000000Z', 'FutureMigration');";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<CliExitException>(() =>
            GrepHandler.Run([$"--output-dir={_outputDir}", "--query=Foo"]));

        Assert.Equal(Message(31), ex.Message);
        Assert.Equal(31, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());
    }

    [Fact]
    public async Task McpAdvancePin_OnStaleSchema_FailsWithMessage_AndKeepsOldStore()
    {
        new MigrationRunner(_dbPath).RunMigrations();
        var snapshotId = "snap-guard";
        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();
            store.SaveWorkspace("ws-guard", "gitroot", "solution.sln");
            store.SaveSnapshot(new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "ws-guard",
                GitRoot = "gitroot",
                SolutionPath = "solution.sln",
                CreatedAtUtc = DateTime.UtcNow
            });
            store.MarkSnapshotComplete(snapshotId);
            store.Close();
        }

        await using var session = McpSessionContext.Create([$"--output-dir={_outputDir}"]);
        Assert.Equal(snapshotId, session.PinnedSnapshotId);

        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM schema_metadata WHERE version > {StaleVersion};";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<CliExitException>(() => session.AdvancePin("snap-other"));

        Assert.Equal(Message(StaleVersion), ex.Message);
        Assert.Equal(snapshotId, session.PinnedSnapshotId);
        Assert.NotNull(session.Store.GetLatestSnapshotId());
    }

    [Fact]
    public async Task McpRefresh_OnNewerSchema_FailsWithMessage_AndDoesNotChangeIt()
    {
        new MigrationRunner(_dbPath).RunMigrations();
        var snapshotId = "snap-refresh-guard";
        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();
            store.SaveWorkspace("ws-guard", "gitroot", "solution.sln");
            store.SaveSnapshot(new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "ws-guard",
                GitRoot = "gitroot",
                SolutionPath = "solution.sln",
                CreatedAtUtc = DateTime.UtcNow
            });
            store.MarkSnapshotComplete(snapshotId);
            store.Close();
        }

        await using var session = McpSessionContext.Create([$"--output-dir={_outputDir}"]);
        Assert.Equal(snapshotId, session.PinnedSnapshotId);

        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_metadata (version, applied_at_utc, migration_id) VALUES (31, '2030-01-01T00:00:00.0000000Z', 'FutureMigration');";
            command.ExecuteNonQuery();
        }

        var tool = new RefreshTool(session);
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpRefresh());

        Assert.Equal(Message(31), ex.Message);
        Assert.Equal(31, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());
        Assert.Equal(snapshotId, session.PinnedSnapshotId);
    }

    [Fact]
    public async Task McpRetract_OnStaleSchema_FailsWithMessage_KeepsAnnotationAndSchema()
    {
        new MigrationRunner(_dbPath).RunMigrations();
        var snapshotId = "snap-retract-guard";
        long annotationId;
        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();
            store.SaveWorkspace("ws-guard", "gitroot", "solution.sln");
            store.SaveSnapshot(new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "ws-guard",
                GitRoot = "gitroot",
                SolutionPath = "solution.sln",
                CreatedAtUtc = DateTime.UtcNow
            });
            store.MarkSnapshotComplete(snapshotId);
            store.SaveAnnotations(snapshotId, new[] { new AnnotationRecord("sym-guard", "note", "guard-v1") });
            annotationId = store.GetAnnotations(snapshotId).Single().AnnotationId;
            store.Close();
        }

        await using var session = McpSessionContext.Create([$"--output-dir={_outputDir}"]);
        Assert.Equal(snapshotId, session.PinnedSnapshotId);

        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM schema_metadata WHERE version > {StaleVersion};";
            command.ExecuteNonQuery();
        }

        var tool = new AnnotationsTool(session);
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpRetractAnnotation(annotation_id: annotationId));

        Assert.Equal(Message(StaleVersion), ex.Message);
        Assert.Equal(StaleVersion, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());

        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();
            Assert.Contains(store.GetAnnotations(snapshotId), a => a.AnnotationId == annotationId);
            store.Close();
        }
    }
}
