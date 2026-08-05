using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public partial class MigrationRunnerTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationRunnerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    internal static SymbolDeclaration MakeDecl(
        string docCommentId,
        string assembly,
        IndexedSymbolKind kind,
        string docVersionId,
        int? fullS, int? fullE,
        int? sigS, int? sigE,
        int? bodyS, int? bodyE,
        int? nameS, int? nameE,
        bool isPartial = false,
        string? fqn = null,
        string? metadataJson = null,
        string? symbolId = null)
    {
        return new SymbolDeclaration
        {
            SymbolId = symbolId != null
            ? new SymbolId(SymbolId.Parse(symbolId).DocCommentId, SymbolId.Parse(symbolId).AssemblyIdentity, fqn)
            : new SymbolId(docCommentId, assembly, fqn),
            Kind = kind,
            DocumentVersionId = docVersionId,
            FullSpan = new DeclarationSpan(fullS, fullE),
            SignatureSpan = new DeclarationSpan(sigS, sigE),
            BodySpan = new DeclarationSpan(bodyS, bodyE),
            NameSpan = new DeclarationSpan(nameS, nameE),
            IsPartial = isPartial,
            MetadataJson = metadataJson
        };
    }

    [Fact]
    public void RunMigrations_AppliesAllMigrations_SchemaVersionIsCurrent()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();

        Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void RunMigrations_CalledTwice_IsIdempotent()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();
        runner.RunMigrations();

        Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());
    }

    [Fact]
    public void Migration017_CreatesUniqueEdgeRelationIndex()
    {
        var runner = new MigrationRunner(_dbPath);

        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info('ux_edges_relation');";
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(2));

        Assert.Equal(
            ["snapshot_id", "source_symbol_id", "target_symbol_id", "kind"],
            columns);
    }

    [Fact]
    public void Migration002_AddsLineStartsColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(document_versions);";
        using var reader = cmd.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "line_starts")
                found = true;
        }
        Assert.True(found, "line_starts column should exist after migration 002");
    }

    [Fact]
    public void SaveAndLoadLatestSnapshot_RoundTripsFields()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var snapshotId = "test-snap-001";
        var workspaceId = "workspace:///home/user/project/src/sln";
        var gitRoot = "/home/user/project";
        var solutionPath = "/home/user/project/src/sln";
        var createdAt = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = gitRoot,
            SolutionPath = solutionPath,
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = createdAt,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new() { DocumentId = "doc1", FilePath = "src/Program.cs", ContentHash = "abc123", Encoding = "utf-8", LineStart = "", CreatedAtUtc = DateTime.MinValue },
                new() { DocumentId = "doc2", FilePath = "src/Utils.cs", ContentHash = "def456", Encoding = "utf-8", LineStart = "", CreatedAtUtc = DateTime.MinValue },
            }
        };

        store.SaveSnapshot(original);
        store.MarkSnapshotComplete(snapshotId);

        var loaded = store.LoadLatestSnapshot(workspaceId);

        Assert.NotNull(loaded);
        Assert.Equal(snapshotId, loaded!.SnapshotId);
        Assert.Equal(workspaceId, loaded.WorkspaceId);
        Assert.Equal(gitRoot, loaded.GitRoot);
        Assert.Equal(solutionPath, loaded.SolutionPath);
        Assert.Equal("10.0.301", loaded.SdkVersion);
        Assert.Equal("4.12.0.0", loaded.CompilerVersion);
        Assert.Equal(createdAt, loaded.CreatedAtUtc);
        Assert.Equal(2, loaded.Documents.Count);

        var doc1 = loaded.Documents[0];
        Assert.Equal("doc1", doc1.DocumentId);
        Assert.Equal("src/Program.cs", doc1.FilePath);
        Assert.Equal("abc123", doc1.ContentHash);

        var doc2 = loaded.Documents[1];
        Assert.Equal("doc2", doc2.DocumentId);
        Assert.Equal("src/Utils.cs", doc2.FilePath);
        Assert.Equal("def456", doc2.ContentHash);
    }

    [Fact]
    public void LoadLatestSnapshot_NoSnapshot_ReturnsNull()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var result = store.LoadLatestSnapshot("workspace:///nonexistent");

        Assert.Null(result);

    }

    [Fact]
    public void SaveAndLoad_ContentRoundTrips()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var snapshotId = "snap-content-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("using System;\n\nclass Foo { }\n");
        var lineStarts = "[0,13,15]";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            }
        };

        store.SaveSnapshot(original);

        var source = store.GetSource("src/Foo.cs", snapshotId);
        Assert.NotNull(source);
        Assert.Equal("using System;\n\nclass Foo { }\n", source);

    }

    [Fact]
    public void SaveSnapshot_WithProjectReferences_RoundTripsAggregate()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var snapshotId = "test-snap-roundtrip";
        var workspaceId = "workspace:///home/user/project/src/sln";
        var gitRoot = "/home/user/project";
        var solutionPath = "/home/user/project/src/sln";

        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("class Foo { }\n");
        var lineStarts = "[0]";
        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = gitRoot,
            SolutionPath = solutionPath,
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
            Projects = new System.Collections.Generic.List<ProjectRow>
            {
                new()
                {
                    Name = "CoreLib",
                    TargetFramework = "net9.0",
                    References = new System.Collections.Generic.List<string> { "DomainLib" },
                },
            },
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            },
        };

        store.SaveSnapshot(original);
        store.MarkSnapshotComplete(snapshotId);

        var loaded = store.LoadLatestSnapshot(workspaceId);

        Assert.NotNull(loaded);
        Assert.Equal(snapshotId, loaded!.SnapshotId);
        Assert.Single(loaded.Projects);
        var project = loaded.Projects[0];
        Assert.Equal("CoreLib", project.Name);
        Assert.Equal("net9.0", project.TargetFramework);
        Assert.Single(project.References);
        Assert.Equal("DomainLib", project.References[0]);
        Assert.Single(loaded.Documents);
        Assert.Equal("src/Foo.cs", loaded.Documents[0].FilePath);

    }

    [Fact]
    public void SaveSnapshot_WhenSnapshotDocumentInsertFails_RollsBackEntireAggregate()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        // Install a trigger that fires on the late snapshot_documents insert and raises an error.
        using (var setupConn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            setupConn.Open();
            using var cmd = setupConn.CreateCommand();
            cmd.CommandText = @"
                CREATE TRIGGER test_fail_snapshot_documents
                BEFORE INSERT ON snapshot_documents
                BEGIN
                    SELECT RAISE(ABORT, 'test induced failure');
                END;
            ";
            cmd.ExecuteNonQuery();
            setupConn.Close();
        }

        var snapshotId = "test-snap-rollback";
        var workspaceId = "workspace:///rollback/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("class Bar { }\n");
        var lineStarts = "[0]";

        var manifest = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/rollback",
            SolutionPath = "/rollback/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Projects = new System.Collections.Generic.List<ProjectRow>
            {
                new() { Name = "ProjA", TargetFramework = "net9.0", References = new System.Collections.Generic.List<string>() },
            },
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/Bar.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            },
        };

        var ex = Assert.ThrowsAny<Exception>(() => store.SaveSnapshot(manifest));
        Assert.Contains("test induced failure", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify that no rows from any aggregate remain.
        using var verifyConn = new SqliteConnection($"Data Source={_dbPath}");
        verifyConn.Open();
        Assert.Equal(0, CountRows(verifyConn, "snapshots", $"snapshot_id = '{snapshotId}'"));
        Assert.Equal(0, CountRows(verifyConn, "projects", "1=1"));
        Assert.Equal(0, CountRows(verifyConn, "project_references", "1=1"));
        Assert.Equal(0, CountRows(verifyConn, "documents", "1=1"));
        Assert.Equal(0, CountRows(verifyConn, "document_versions", "1=1"));
        Assert.Equal(0, CountRows(verifyConn, "snapshot_documents", "1=1"));
        verifyConn.Close();

        // Drop the trigger so subsequent tests are not affected.
        using (var dropConn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            dropConn.Open();
            using var dropCmd = dropConn.CreateCommand();
            dropCmd.CommandText = "DROP TRIGGER IF EXISTS test_fail_snapshot_documents;";
            dropCmd.ExecuteNonQuery();
            dropConn.Close();
        }

    }

    private static int CountRows(SqliteConnection conn, string table, string where)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void GetSource_MissingDocument_ReturnsNull()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var result = store.GetSource("nonexistent.cs", "snap-none");

        Assert.Null(result);

    }

    [Fact]
    public void GetSource_MissingSnapshot_ReturnsNull()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var result = store.GetSource("src/Foo.cs", "non-existent-snapshot");

        Assert.Null(result);

    }

    [Fact]
    public void GetSource_NoRoslyn_ReturnsContentFromSqliteOnly()
    {
        var snapshotId = "snap-noroslyn-001";

        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();
            store.RunMigrations();

            var workspaceId = "workspace:///root/proj";
            var sourceBytes = System.Text.Encoding.UTF8.GetBytes("console.log('hello');");
            var lineStarts = "[0,22]";

            var original = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = workspaceId,
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new System.Collections.Generic.List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/app.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(original);
        }

        using (var reopened = new SqliteIndexStore(_dbPath))
        {
            reopened.Open();

            var source = reopened.GetSource("src/app.cs", snapshotId);
            Assert.NotNull(source);
            Assert.Equal("console.log('hello');", source);
        }
    }

    [Fact]
    public void LineStarts_FirstOffsetIsZero()
    {

        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var snapshotId = "snap-linestarts-001";
        var workspaceId = "workspace:///root/proj";
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes("line1\nline2\nline3\n");
        var lineStarts = "[0,6,12,18]";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {
                new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/multi.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
            }
        };
        store.SaveSnapshot(original);
        store.MarkSnapshotComplete(snapshotId);

        var loaded = store.LoadLatestSnapshot(workspaceId);
        Assert.NotNull(loaded);
        var doc = loaded!.Documents[0];
        Assert.Equal("[0,6,12,18]", doc.LineStart);

    }

    [Fact]
    public void Content_WithNullContent_StoresNull()
    {
        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        var snapshotId = "snap-nullcontent-001";
        var workspaceId = "workspace:///root/proj";

        var original = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = workspaceId,
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new System.Collections.Generic.List<DocumentVersion>
            {

                new() { DocumentId = "doc1", FilePath = "src/empty.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = "", CreatedAtUtc = DateTime.MinValue },
            }
        };
        store.SaveSnapshot(original);

        var source = store.GetSource("src/empty.cs", snapshotId);
        Assert.Null(source);

    }

    [Fact]
    public void Migration002_AppliedOnExistingMigration001_Database()
    {

        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();
        Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(document_versions);";
        using var reader = cmd.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "line_starts")
                found = true;
        }
        Assert.True(found, "line_starts column must exist after upgrade path");
    }

    [Fact]
    public void Migration010_AddsLastChangedSnapshotIdColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(documents);";
        using var reader = cmd.ExecuteReader();
        bool found = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "last_changed_snapshot_id")
                found = true;
        }
        Assert.True(found, "last_changed_snapshot_id column should exist after migration 010");
    }

}
