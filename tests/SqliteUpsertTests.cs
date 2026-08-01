using Microsoft.Data.Sqlite;

namespace Lurp.Storage.Tests;

public sealed class SqliteUpsertTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"indexer_upsert_{Guid.NewGuid():N}.db");
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

    private T Scalar<T>(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    [Fact]
    public void SaveWorkspace_WhenWorkspaceExists_UpdatesColumnsWithoutReplacingRow()
    {
        var store = CreateStore();
        var workspaceId = "ws-upsert-1";

        store.SaveWorkspace(workspaceId, "/root1", "/root1/test.sln", DateTime.UtcNow);
        var rowid = Scalar<long>($"SELECT rowid FROM workspaces WHERE workspace_id = '{workspaceId}';");

        store.SaveWorkspace(workspaceId, "/root2", "/root2/test.sln", DateTime.UtcNow);
        var rowid2 = Scalar<long>($"SELECT rowid FROM workspaces WHERE workspace_id = '{workspaceId}';");
        var gitRoot = Scalar<string>($"SELECT git_root FROM workspaces WHERE workspace_id = '{workspaceId}';");
        var solutionPath = Scalar<string>($"SELECT solution_path FROM workspaces WHERE workspace_id = '{workspaceId}';");

        Assert.Equal(rowid, rowid2);
        Assert.Equal("/root2", gitRoot);
        Assert.Equal("/root2/test.sln", solutionPath);
    }

    [Fact]
    public void SaveSnapshot_WhenWorkspaceExists_PreservesWorkspaceRowIdentity()
    {
        var store = CreateStore();
        var workspaceId = "ws-upsert-2";

        var manifest1 = new SnapshotRow
        {
            SnapshotId = "snap-upsert-1",
            WorkspaceId = workspaceId,
            GitRoot = "/root1",
            SolutionPath = "/root1/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = [],
        };
        store.SaveSnapshot(manifest1);

        var rowid = Scalar<long>($"SELECT rowid FROM workspaces WHERE workspace_id = '{workspaceId}';");

        var manifest2 = new SnapshotRow
        {
            SnapshotId = "snap-upsert-2",
            WorkspaceId = workspaceId,
            GitRoot = "/root2",
            SolutionPath = "/root2/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = [],
        };
        store.SaveSnapshot(manifest2);

        var rowid2 = Scalar<long>($"SELECT rowid FROM workspaces WHERE workspace_id = '{workspaceId}';");
        var gitRoot = Scalar<string>($"SELECT git_root FROM workspaces WHERE workspace_id = '{workspaceId}';");
        var solutionPath = Scalar<string>($"SELECT solution_path FROM workspaces WHERE workspace_id = '{workspaceId}';");

        Assert.Equal(rowid, rowid2);
        Assert.Equal("/root2", gitRoot);
        Assert.Equal("/root2/test.sln", solutionPath);
    }

    [Fact]
    public void SaveDeclarations_WhenSymbolExists_UpdatesColumnsWithoutReplacingRow()
    {
        var store = CreateStore();

        var manifest = new SnapshotRow
        {
            SnapshotId = "snap-decl-1",
            WorkspaceId = "ws-decl-1",
            GitRoot = "/decl",
            SolutionPath = "/decl/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = [],
        };
        store.SaveSnapshot(manifest);

        // Seed document and document_version rows so declarations FK constraints pass
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO documents (document_id, relative_path, last_changed_snapshot_id)
                VALUES ('doc:abc', 'test.cs', 'snap-decl-1');
            ";
            command.ExecuteNonQuery();

            command.CommandText = @"
                INSERT INTO document_versions (document_version_id, document_id, content_hash)
                VALUES ('doc:abc:hash1', 'doc:abc', 'hash1');
            ";
            command.ExecuteNonQuery();
        }

        var symbolId = new SymbolId("T:Ns.Class1", "asm1", "Ns.Class1");
        var decl1 = new SymbolDeclaration
        {
            SymbolId = symbolId,
            Kind = IndexedSymbolKind.NamedType,
            DocumentVersionId = "doc:abc:hash1",
            FullSpan = new DeclarationSpan(0, 100),
            SignatureSpan = new DeclarationSpan(10, 50),
            BodySpan = new DeclarationSpan(30, 90),
            NameSpan = new DeclarationSpan(15, 21),
            MetadataJson = "{\"original\": true}",
        };

        // Disable FK checks during declaration save (matches project convention)
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = OFF;";
            pragma.ExecuteNonQuery();
        }

        store.SaveDeclarations("snap-decl-1", [decl1]);
        var rowid = Scalar<long>($"SELECT rowid FROM symbols WHERE symbol_id = '{symbolId.Value}';");

        var decl2 = new SymbolDeclaration
        {
            SymbolId = symbolId,
            Kind = IndexedSymbolKind.NamedType,
            DocumentVersionId = "doc:abc:hash1",
            FullSpan = new DeclarationSpan(0, 100),
            SignatureSpan = new DeclarationSpan(10, 50),
            BodySpan = new DeclarationSpan(30, 90),
            NameSpan = new DeclarationSpan(15, 21),
            MetadataJson = "{\"updated\": true}",
        };
        store.SaveDeclarations("snap-decl-1", [decl2]);
        var rowid2 = Scalar<long>($"SELECT rowid FROM symbols WHERE symbol_id = '{symbolId.Value}';");
        var metadata = Scalar<string>($"SELECT metadata_json FROM symbols WHERE symbol_id = '{symbolId.Value}';");

        Assert.Equal(rowid, rowid2);
        Assert.Equal("{\"updated\": true}", metadata);
    }

    [Fact]
    public void SaveSemanticChanges_WhenChangeExists_UpdatesColumnsWithoutReplacingRow()
    {
        var store = CreateStore();

        var changeId = "change-upsert-1";
        var change1 = new SemanticChange
        {
            ChangeId = changeId,
            FromSnapshotId = "snap-a",
            ToSnapshotId = "snap-b",
            ChangeType = "SymbolAdded",
            SymbolId = "T:Ns.Class1|asm1",
            DetailJson = "{\"first\": true}",
            CreatedAtUtc = DateTime.UtcNow,
        };
        store.SaveSemanticChanges("snap-a", "snap-b", [change1]);
        var rowid = Scalar<long>($"SELECT rowid FROM semantic_changes WHERE change_id = '{changeId}';");

        var change2 = new SemanticChange
        {
            ChangeId = changeId,
            FromSnapshotId = "snap-a",
            ToSnapshotId = "snap-b",
            ChangeType = "SymbolAdded",
            SymbolId = "T:Ns.Class1|asm1",
            DetailJson = "{\"second\": true}",
            CreatedAtUtc = DateTime.UtcNow,
        };
        store.SaveSemanticChanges("snap-a", "snap-b", [change2]);
        var rowid2 = Scalar<long>($"SELECT rowid FROM semantic_changes WHERE change_id = '{changeId}';");
        var detail = Scalar<string>($"SELECT detail_json FROM semantic_changes WHERE change_id = '{changeId}';");

        Assert.Equal(rowid, rowid2);
        Assert.Equal("{\"second\": true}", detail);
    }

    [Fact]
    public void SaveSnapshotSymbols_WhenMembershipExists_UpdatesColumnsWithoutReplacingRow()
    {
        var store = CreateStore();

        var snapshotId = "snap-sym-1";
        var manifest = new SnapshotRow
        {
            SnapshotId = snapshotId,
            WorkspaceId = "ws-sym-1",
            GitRoot = "/sym",
            SolutionPath = "/sym/test.sln",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = [],
        };
        store.SaveSnapshot(manifest);

        var symbolId = "T:Ns.Member1|asm1";

        // Seed a symbol row so the SELECT in SaveSnapshotSymbols can read from snapshot_symbols
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind, metadata_json, fqn)
                VALUES (@symbolId, @docCommentId, 'asm1', 'Method', '{""initial"": true}', 'Ns.Member1');
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@docCommentId", "M:Ns.Member1");
            command.ExecuteNonQuery();

            // Insert an anchor snapshot_symbols entry so the subselect can find fqn/metadata_json
            command.CommandText = @"
                INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                VALUES (@snapshotId, @symbolId, @fqn, @metadataJson);
            ";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@fqn", "Ns.Member1");
            command.Parameters.AddWithValue("@metadataJson", "{\"initial\": true}");
            command.ExecuteNonQuery();
        }

        var rowid = Scalar<long>(
            $"SELECT rowid FROM snapshot_symbols WHERE snapshot_id = '{snapshotId}' AND symbol_id = '{symbolId}';");

        // Update metadata_json via symbols table, then re-save snapshot_symbols to trigger the upsert
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE symbols SET metadata_json = @metadataJson WHERE symbol_id = @symbolId;
                UPDATE snapshot_symbols SET metadata_json = @metadataJson
                    WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId;
            ";
            command.Parameters.AddWithValue("@metadataJson", "{\"updated\": true}");
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.ExecuteNonQuery();
        }

        store.SaveSnapshotSymbols(snapshotId, [symbolId]);

        var rowid2 = Scalar<long>(
            $"SELECT rowid FROM snapshot_symbols WHERE snapshot_id = '{snapshotId}' AND symbol_id = '{symbolId}';");
        var metadata = Scalar<string>(
            $"SELECT metadata_json FROM snapshot_symbols WHERE snapshot_id = '{snapshotId}' AND symbol_id = '{symbolId}';");

        Assert.Equal(rowid, rowid2);
        Assert.Equal("{\"updated\": true}", metadata);
    }

    [Fact]
    public void CopySnapshotSymbols_WhenTargetMembershipExists_UpdatesColumnsWithoutReplacingRow()
    {
        var store = CreateStore();

        var fromSnapshotId = "snap-copy-from";
        var toSnapshotId = "snap-copy-to";

        foreach (var sid in new[] { fromSnapshotId, toSnapshotId })
        {
            var manifest = new SnapshotRow
            {
                SnapshotId = sid,
                WorkspaceId = "ws-copy-1",
                GitRoot = "/copy",
                SolutionPath = "/copy/test.sln",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = [],
            };
            store.SaveSnapshot(manifest);
        }

        var symbolId = "T:Ns.CopyTarget|asm1";

        // Seed symbols and snapshot_symbols in the source snapshot
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind, metadata_json, fqn)
                VALUES (@symbolId, @docCommentId, 'asm1', 'Class', '{""first"": true}', 'Ns.CopyTarget');
            ";
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@docCommentId", "T:Ns.CopyTarget");
            command.ExecuteNonQuery();

            command.CommandText = @"
                INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn, metadata_json)
                VALUES (@snapshotId, @symbolId, 'Ns.CopyTarget', '{""first"": true}');
            ";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@snapshotId", fromSnapshotId);
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.ExecuteNonQuery();
        }

        // First copy creates the target row
        store.CopySnapshotSymbols(fromSnapshotId, toSnapshotId);
        var rowid = Scalar<long>(
            $"SELECT rowid FROM snapshot_symbols WHERE snapshot_id = '{toSnapshotId}' AND symbol_id = '{symbolId}';");

        // Update source metadata so target would see a different value on re-copy
        using (var connection = new SqliteConnection($"Data Source={_dbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                UPDATE symbols SET metadata_json = @metadataJson WHERE symbol_id = @symbolId;
                UPDATE snapshot_symbols SET metadata_json = @metadataJson
                    WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId;
            ";
            command.Parameters.AddWithValue("@metadataJson", "{\"second\": true}");
            command.Parameters.AddWithValue("@symbolId", symbolId);
            command.Parameters.AddWithValue("@snapshotId", fromSnapshotId);
            command.ExecuteNonQuery();
        }

        // Second copy triggers the upsert on target
        store.CopySnapshotSymbols(fromSnapshotId, toSnapshotId);
        var rowid2 = Scalar<long>(
            $"SELECT rowid FROM snapshot_symbols WHERE snapshot_id = '{toSnapshotId}' AND symbol_id = '{symbolId}';");
        var metadata = Scalar<string>(
            $"SELECT metadata_json FROM snapshot_symbols WHERE snapshot_id = '{toSnapshotId}' AND symbol_id = '{symbolId}';");

        Assert.Equal(rowid, rowid2);
        Assert.Equal("{\"second\": true}", metadata);
    }
}
