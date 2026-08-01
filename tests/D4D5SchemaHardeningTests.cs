using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class D4D5SchemaHardeningTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_schema_hardening_{Guid.NewGuid():N}.db");
    private SqliteIndexStore? _store;

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private SqliteIndexStore CreateStore()
    {
        _store?.Dispose();
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        return _store;
    }

    private static void InsertPrerequisiteRow(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void DocumentVersions_UpdateIsRejected()
    {
        var store = CreateStore();

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();

        InsertPrerequisiteRow(connection,
            "INSERT INTO workspaces (workspace_id, git_root, solution_path) VALUES ('ws1', '/ws', '/ws/test.sln');");
        InsertPrerequisiteRow(connection,
            "INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc) VALUES ('snap1', 'ws1', '2026-07-27T00:00:00');");
        InsertPrerequisiteRow(connection,
            "INSERT INTO documents (document_id, relative_path) VALUES ('doc1', 'src/Foo.cs');");
        InsertPrerequisiteRow(connection,
            "INSERT INTO document_versions (document_version_id, document_id, content_hash, content, byte_count) VALUES ('doc1:hash1', 'doc1', 'hash1', X'DEADBEEF', 4);");

        using var updateCmd = connection.CreateCommand();
        updateCmd.CommandText = @"
            UPDATE document_versions
            SET content = X'00'
            WHERE document_version_id = 'doc1:hash1';
        ";

        var ex = Assert.Throws<SqliteException>(() => updateCmd.ExecuteNonQuery());
        Assert.Contains("immutable", ex.Message);

        using var verifyCmd = connection.CreateCommand();
        verifyCmd.CommandText = "SELECT content FROM document_versions WHERE document_version_id = 'doc1:hash1';";
        var content = (byte[])verifyCmd.ExecuteScalar()!;
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, content);
    }

    [Fact]
    public void SnapshotDocuments_DuplicatePairIsIgnored()
    {
        var store = CreateStore();

        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();

        InsertPrerequisiteRow(connection,
            "INSERT INTO workspaces (workspace_id, git_root, solution_path) VALUES ('ws1', '/ws', '/ws/test.sln');");
        InsertPrerequisiteRow(connection,
            "INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc) VALUES ('snap1', 'ws1', '2026-07-27T00:00:00');");
        InsertPrerequisiteRow(connection,
            "INSERT INTO documents (document_id, relative_path) VALUES ('doc1', 'src/Foo.cs');");
        InsertPrerequisiteRow(connection,
            "INSERT INTO document_versions (document_version_id, document_id, content_hash, content, byte_count) VALUES ('doc1:hash1', 'doc1', 'hash1', X'DEADBEEF', 4);");
        InsertPrerequisiteRow(connection,
            "INSERT INTO snapshot_documents (snapshot_id, document_version_id) VALUES ('snap1', 'doc1:hash1');");

        Assert.Equal(1, CountRows(connection, "snapshot_documents"));

        using var duplicateCmd = connection.CreateCommand();
        duplicateCmd.CommandText = @"
            INSERT INTO snapshot_documents (snapshot_id, document_version_id)
            VALUES ('snap1', 'doc1:hash1');
        ";

        var ex = Assert.Throws<SqliteException>(() => duplicateCmd.ExecuteNonQuery());
        Assert.Contains("UNIQUE", ex.Message);

        Assert.Equal(1, CountRows(connection, "snapshot_documents"));
    }
}
