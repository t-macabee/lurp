using System.Globalization;
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
        TestDatabase.Delete(_dbPath);
    }

    [Fact]
    public void MigrationList_MatchesTheMigrationFiles_AndLatestVersionIsTheHighest()
    {
        // Oracle: the migration source files on disk (D105), one file Migration_NNN_<Name>.cs for
        // each migration, numbered 1..N without a gap. The registered list, the migration types in
        // the assembly and LatestVersion must all agree with the files.
        var files = Directory.GetFiles(Path.Combine(TestRepoRoot.Find(), "src", "Storage", "Migrations"), "Migration_*.cs")
            .Select(static path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToList();
        var fileVersions = files
            .Select(static name => int.Parse(name.AsSpan("Migration_".Length, 3), CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(Enumerable.Range(1, files.Count), fileVersions);
        Assert.Equal(fileVersions, MigrationRunner.MigrationVersions.Order());
        Assert.Equal(fileVersions[^1], MigrationRunner.LatestVersion);

        var typeNames = typeof(MigrationRunner).Assembly
            .GetTypes()
            .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
            .Select(static t => t.Name)
            .Order(StringComparer.Ordinal);
        Assert.Equal(files, typeNames);
    }

    [Fact]
    public void RoundTrip_AllMigrations_ProducesCurrentSchema()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        var current = runner.GetCurrentSchemaVersion();
        Assert.Equal(MigrationRunner.LatestVersion, current);
    }

    [Fact]
    public void RoundTrip_SnapshotTable_HasStatusColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('snapshots') WHERE name = 'status';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_EdgesTable_HasReceiverTypeConstraints()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

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

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('annotations') WHERE name = 'document_path';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_ProjectsTable_HasCompilationInputColumns()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name IN ('metadata_reference_identities', 'compilation_options_fingerprint');";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_ProjectsTable_HasAssemblyNameColumn()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name = 'assembly_name';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_Migration035_DeclarationIndexSet()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('idx_edges_snapshot_id', 'idx_annotations_snapshot_id');";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('idx_declarations_doc_version_generated', 'idx_declarations_doc_version', 'idx_declarations_generated', 'idx_declarations_symbol_id', 'idx_snapshot_symbols_snapshot_id', 'idx_snapshot_documents_snapshot_id');";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_Migration036_EdgeIndexSet()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('idx_edges_snapshot_id', 'idx_edges_snapshot_target', 'idx_edges_document', 'ux_edges_relation');";
        Assert.Equal(4L, (long)command.ExecuteScalar()!);

        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name IN ('idx_edges_source', 'idx_edges_target');";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void RoundTrip_Migration037_DocumentVersionsDocumentIdIndex()
    {
        var runner = new MigrationRunner(_dbPath);
        runner.RunMigrations();

        using var connection = TestDatabase.Open(_dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT tbl_name FROM sqlite_master WHERE type = 'index' AND name = 'idx_document_versions_document_id';";
        Assert.Equal("document_versions", command.ExecuteScalar());

        command.CommandText = "SELECT name FROM pragma_index_info('idx_document_versions_document_id');";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(0));
        Assert.Single(columns);
        Assert.Equal("document_id", columns[0]);
    }

    [Fact]
    public void RunMigrations_OnNewerSchema_ThrowsAndAppliesNothing()
    {
        new MigrationRunner(_dbPath).RunMigrations();
        SqliteConnection.ClearAllPools();

        var futureVersion = MigrationRunner.LatestVersion + 1;
        using (var connection = TestDatabase.Open(_dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO schema_metadata (version, applied_at_utc, migration_id) VALUES ({futureVersion}, '2030-01-01T00:00:00.0000000Z', 'FutureMigration');";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<NewerSchemaException>(() => new MigrationRunner(_dbPath).RunMigrations());
        Assert.Equal(SchemaVersionMessages.Newer(_dbPath, futureVersion, MigrationRunner.LatestVersion), ex.Message);
        Assert.Equal(futureVersion, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());

        using var verify = TestDatabase.OpenReadOnly(_dbPath);
        using var countCmd = verify.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM schema_metadata;";
        Assert.Equal((long)futureVersion, (long)countCmd.ExecuteScalar()!);
    }

    [Fact]
    public void ForwardMigration_FromV1Schema_PreservesSeededData()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-e4-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
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
            Assert.Equal(MigrationRunner.LatestVersion, finalVersion);

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
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV1Schema_RecomposesReferencedDocumentVersionId()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-e4-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
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

                    INSERT INTO snapshot_documents (snapshot_id, document_version_id)
                    VALUES ('snap-e4', 'dv-e4');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            var finalVersion = new MigrationRunner(dbPath).GetCurrentSchemaVersion();
            Assert.Equal(MigrationRunner.LatestVersion, finalVersion);

            using var verify = TestDatabase.OpenReadOnly(dbPath);

            using var childCommand = verify.CreateCommand();
            childCommand.CommandText = "SELECT document_version_id FROM snapshot_documents WHERE snapshot_id = 'snap-e4';";
            Assert.Equal("doc-e4:abc123", childCommand.ExecuteScalar());

            using var parentCommand = verify.CreateCommand();
            parentCommand.CommandText = "SELECT document_version_id FROM document_versions WHERE document_id = 'doc-e4';";
            Assert.Equal("doc-e4:abc123", parentCommand.ExecuteScalar());
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    // This test re-runs the newest migration (047). When a migration is added, move the test to it; that migration must be safe to run twice.
    [Fact]
    public void RunMigrations_ThrowsAndRollsBack_WhenTheLastMigrationLeavesAForeignKeyViolation()
    {
        new MigrationRunner(_dbPath).RunMigrations();
        SqliteConnection.ClearAllPools();

        using (var connection = TestDatabase.Open(_dbPath))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                DELETE FROM schema_metadata WHERE version = 47;
                DROP INDEX IF EXISTS idx_snapshot_symbols_bare_fqn;
                DROP INDEX IF EXISTS idx_snapshot_symbols_fqn;
                ALTER TABLE snapshot_symbols DROP COLUMN bare_fqn;
                INSERT INTO snapshot_documents (snapshot_id, document_version_id) VALUES ('no-such-snapshot', 'no-such-version');
                """;
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(() => new MigrationRunner(_dbPath).RunMigrations());
        Assert.Contains("snapshot_documents", ex.Message);
        Assert.Equal(MigrationRunner.LatestVersion - 1, new MigrationRunner(_dbPath).GetCurrentSchemaVersion());

        using var verify = TestDatabase.OpenReadOnly(_dbPath);
        using var columnCommand = verify.CreateCommand();
        columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('snapshot_symbols') WHERE name = 'bare_fqn';";
        Assert.Equal(0L, (long)columnCommand.ExecuteScalar()!);
    }

    [Fact]
    public void ForwardMigration_FromV37_LinksDeclarationsToTheSnapshotsThatShowThem()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v37-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo37 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 37)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo37)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (37, '2025-01-01T00:00:00.0000000Z', 'Migration_037_DocumentVersionsDocumentIdIndex');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 37, 6),
                           ('s2', 'ws1', '2025-01-02T00:00:00.0000000Z', '10.0.100', 37, 6);

                    INSERT INTO documents (document_id, relative_path)
                    VALUES ('doc1', 'src/A.cs'), ('doc2', 'src/B.cs');

                    INSERT INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count)
                    VALUES ('dv1', 'doc1', 'h1', X'01', 'utf-8', 1),
                           ('dv2', 'doc2', 'h2', X'02', 'utf-8', 1);

                    INSERT INTO snapshot_documents (snapshot_id, document_version_id)
                    VALUES ('s1', 'dv1'), ('s2', 'dv1'), ('s2', 'dv2');

                    INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind)
                    VALUES ('a', 'T:N.A', 'Asm', 'Class'),
                           ('b', 'T:N.B', 'Asm', 'Class'),
                           ('c', 'T:N.C', 'Asm', 'Class'),
                           ('z', 'T:N.Z', 'Asm', 'Class');

                    INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn)
                    VALUES ('s1', 'a', 'global::N.A'),
                           ('s2', 'b', 'global::N.B'),
                           ('s2', 'c', 'global::N.C');

                    INSERT INTO declarations (declaration_id, symbol_id, document_version_id, full_start, full_end)
                    VALUES (1, 'a', 'dv1', 0, 10),
                           (2, 'b', 'dv1', 0, 10),
                           (3, 'c', 'dv2', 0, 10),
                           (4, 'z', 'dv2', 0, 10);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var query = verify.CreateCommand();
            query.CommandText = "SELECT snapshot_id, declaration_id FROM snapshot_declarations ORDER BY snapshot_id, declaration_id;";
            var links = new List<(string SnapshotId, long DeclarationId)>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
                links.Add((reader.GetString(0), reader.GetInt64(1)));

            Assert.Equal(
                new (string, long)[] { ("s1", 1), ("s2", 2), ("s2", 3) },
                links);
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV37_BackfillsDocumentIsGenerated()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v37-gen-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo37 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 37)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo37)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (37, '2025-01-01T00:00:00.0000000Z', 'Migration_037_DocumentVersionsDocumentIdIndex');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 37, 6);

                    INSERT INTO documents (document_id, relative_path)
                    VALUES ('doc1', 'src/Normal.cs'),
                           ('doc2', 'src/HeaderGen.cs'),
                           ('doc3', 'src/Empty.g.cs'),
                           ('doc4', 'src/Temporary_GeneratedFileX.cs');

                    INSERT INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count)
                    VALUES ('dv1', 'doc1', 'h1', X'01', 'utf-8', 1),
                           ('dv2', 'doc2', 'h2', X'02', 'utf-8', 1),
                           ('dv3', 'doc3', 'h3', X'03', 'utf-8', 1),
                           ('dv4', 'doc4', 'h4', X'04', 'utf-8', 1);

                    INSERT INTO snapshot_documents (snapshot_id, document_version_id)
                    VALUES ('s1', 'dv1'), ('s1', 'dv2'), ('s1', 'dv3'), ('s1', 'dv4');

                    INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind)
                    VALUES ('sym1', 'T:N.Normal', 'Asm', 'Class'),
                           ('sym2', 'T:N.HeaderGen', 'Asm', 'Class');

                    INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn)
                    VALUES ('s1', 'sym1', 'global::N.Normal'),
                           ('s1', 'sym2', 'global::N.HeaderGen');

                    INSERT INTO declarations (declaration_id, symbol_id, document_version_id, full_start, full_end, is_generated)
                    VALUES (1, 'sym1', 'dv1', 0, 10, 0),
                           (2, 'sym2', 'dv2', 0, 10, 1);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var runner = new MigrationRunner(dbPath);
            runner.RunMigrations();
            Assert.Equal(MigrationRunner.LatestVersion, runner.GetCurrentSchemaVersion());

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var query = verify.CreateCommand();
            query.CommandText = """
                SELECT dv.document_version_id, dv.is_generated
                FROM document_versions dv
                ORDER BY dv.document_version_id;
                """;
            var flags = new List<(string Id, int IsGenerated)>();
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                    flags.Add((reader.GetString(0), reader.GetInt32(1)));
            }

            Assert.Equal(
                new (string, int)[] { ("dv1", 0), ("dv2", 1), ("dv3", 1), ("dv4", 0) },
                flags);

            using var idxQuery = verify.CreateCommand();
            idxQuery.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_document_versions_generated';";
            Assert.Equal(1L, (long)idxQuery.ExecuteScalar()!);

            idxQuery.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_declarations_doc_version_generated';";
            Assert.Equal(0L, (long)idxQuery.ExecuteScalar()!);
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV38_DropsTheWriteOnlyColumnsAndTables()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v38-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo38 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 38)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo38)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (38, '2025-01-01T00:00:00.0000000Z', 'Migration_038_SnapshotDeclarations');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 38, 6);

                    INSERT INTO documents (document_id, relative_path, last_changed_snapshot_id)
                    VALUES ('doc1', 'src/A.cs', 's1');

                    INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind, metadata_json, fqn)
                    VALUES ('sym1', 'T:N.A', 'Asm', 'NamedType', '{"access":"public"}', 'global::N.A');

                    INSERT INTO graph_nodes (node_id, node_kind)
                    VALUES ('route://GET /items', 'Route');

                    INSERT INTO snapshot_graph_nodes (snapshot_id, node_id)
                    VALUES ('s1', 'route://GET /items');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            using var verify = TestDatabase.OpenReadOnly(dbPath);

            var docCols = new List<string>();
            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info('documents') ORDER BY cid;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) docCols.Add(r.GetString(0));
            }
            Assert.DoesNotContain("last_changed_snapshot_id", docCols);
            Assert.Equal(["document_id", "relative_path"], docCols);

            var symCols = new List<string>();
            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM pragma_table_info('symbols') ORDER BY cid;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) symCols.Add(r.GetString(0));
            }
            Assert.DoesNotContain("fqn", symCols);
            Assert.DoesNotContain("metadata_json", symCols);
            Assert.Equal(["symbol_id", "doc_comment_id", "assembly_identity", "kind"], symCols);

            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('graph_nodes', 'partial_declarations', 'idx_symbols_fqn');";
                Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
            }

            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT relative_path FROM documents WHERE document_id = 'doc1';";
                Assert.Equal("src/A.cs", cmd.ExecuteScalar());
            }

            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT doc_comment_id || '|' || assembly_identity || '|' || kind FROM symbols WHERE symbol_id = 'sym1';";
                Assert.Equal("T:N.A|Asm|NamedType", cmd.ExecuteScalar());
            }

            using (var cmd = verify.CreateCommand())
            {
                cmd.CommandText = "SELECT node_id FROM snapshot_graph_nodes WHERE snapshot_id = 's1';";
                Assert.Equal("route://GET /items", cmd.ExecuteScalar());
            }
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV39_BackfillsOneSiteForEachLocatedEdge()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v39-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo39 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 39)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo39)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (39, '2025-01-01T00:00:00.0000000Z', 'Migration_039_DropWriteOnlyColumns');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 39, 6);

                    INSERT INTO edges (edge_id, snapshot_id, source_symbol_id, target_symbol_id, kind, provenance,
                                       source_document_path, source_start_line, source_start_column, source_end_line, source_end_column)
                    VALUES (1, 's1', 'src1', 'tgt1', 'Calls', 'compiler_proved', 'a.cs', 1, 2, 1, 10),
                           (2, 's1', 'src2', 'tgt2', 'Calls', 'compiler_proved', 'b.cs', 3, 4, 3, 12),
                           (3, 's1', 'src3', 'tgt3', 'Declares', 'compiler_proved', NULL, NULL, NULL, NULL, NULL);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var query = verify.CreateCommand();
            query.CommandText = """
                SELECT edge_id, document_path, start_line, start_column, end_line, end_column
                FROM edge_sites
                ORDER BY edge_id, document_path, start_line, start_column, end_line, end_column;
                """;
            var rows = new List<(long EdgeId, string Doc, int Sl, int Sc, int El, int Ec)>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)));

            Assert.Equal(
                new (long, string, int, int, int, int)[]
                {
                    (1, "a.cs", 1, 2, 1, 10),
                    (2, "b.cs", 3, 4, 3, 12)
                },
                rows);
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV40_SetsSelectedAtToBuiltAt()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v40-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo40 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 40)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo40)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (40, '2026-02-01T00:00:00.0000000Z', 'Migration_040_EdgeSites');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2026-01-01T00:00:00.0000000Z', '10.0.100', 40, 6),
                           ('s2', 'ws1', '2026-02-01T00:00:00.0000000Z', '10.0.100', 40, 6);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var query = verify.CreateCommand();
            query.CommandText = "SELECT snapshot_id, built_at_utc, selected_at_utc FROM snapshots ORDER BY snapshot_id;";
            var rows = new List<(string Id, string BuiltAt, string? SelectedAt)>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));

            Assert.Equal(2, rows.Count);
            Assert.Equal("s1", rows[0].Id);
            Assert.Equal("2026-01-01T00:00:00.0000000Z", rows[0].BuiltAt);
            Assert.Equal(rows[0].BuiltAt, rows[0].SelectedAt);
            Assert.Equal("s2", rows[1].Id);
            Assert.Equal("2026-02-01T00:00:00.0000000Z", rows[1].BuiltAt);
            Assert.Equal(rows[1].BuiltAt, rows[1].SelectedAt);
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV41_MarksExistingSnapshotsIncremental()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v41-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo41 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 41)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo41)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (41, '2026-02-01T00:00:00.0000000Z', 'Migration_041_SnapshotSelectedAt');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, selected_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', '10.0.100', 41, 6),
                           ('s2', 'ws1', '2026-02-01T00:00:00.0000000Z', '2026-02-01T00:00:00.0000000Z', '10.0.100', 41, 6);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            new MigrationRunner(dbPath).RunMigrations();

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var query = verify.CreateCommand();
            query.CommandText = "SELECT snapshot_id, build_strategy FROM snapshots ORDER BY snapshot_id;";
            var rows = new List<(string Id, string Strategy)>();
            using var reader = query.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));

            Assert.Equal(
                new (string, string)[] { ("s1", "incremental"), ("s2", "incremental") },
                rows);
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void ForwardMigration_FromV42_DeletesDuplicateExtractorAnnotationsAndKeepsUserAnnotations()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v42-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo42 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 42)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo42)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (42, '2026-02-01T00:00:00.0000000Z', 'Migration_042_SnapshotBuildStrategy');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, selected_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s', 'ws1', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z', '10.0.100', 42, 6),
                           ('t', 'ws1', '2026-02-01T00:00:00.0000000Z', '2026-02-01T00:00:00.0000000Z', '10.0.100', 42, 6);

                    INSERT INTO annotations (annotation_id, snapshot_id, symbol_id, kind, value, document_path)
                    VALUES (1, 's', 'Y', 'k', 'v', 'a.cs'),
                           (2, 's', 'Y', 'k', 'v', 'a.cs'),
                           (3, 's', 'Y', 'k', 'v', 'a.cs'),
                           (4, 's', 'Y', 'k', 'v', NULL),
                           (5, 's', 'Y', 'k', 'v', NULL),
                           (6, 's', 'Y', 'k', 'v', 'b.cs'),
                           (7, 't', 'Y', 'k', 'v', 'a.cs');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var runner = new MigrationRunner(dbPath);
            runner.RunMigrations();
            Assert.Equal(MigrationRunner.LatestVersion, runner.GetCurrentSchemaVersion());

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var idsCmd = verify.CreateCommand();
            idsCmd.CommandText = "SELECT annotation_id FROM annotations ORDER BY annotation_id;";
            var remainingIds = new List<long>();
            using (var reader = idsCmd.ExecuteReader())
                while (reader.Read())
                    remainingIds.Add(reader.GetInt64(0));

            Assert.Equal(new long[] { 1, 4, 5, 6, 7 }, remainingIds);

            using var idxCmd = verify.CreateCommand();
            idxCmd.CommandText = "SELECT \"unique\", partial FROM pragma_index_list('annotations') WHERE name = 'idx_annotations_extractor_fact';";
            using var idxReader = idxCmd.ExecuteReader();
            Assert.True(idxReader.Read());
            Assert.Equal(1L, idxReader.GetInt64(0));
            Assert.Equal(1L, idxReader.GetInt64(1));
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void RoundTrip_Migration045_ProjectInternalsVisibleTo()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v37-ivt-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo37 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 37)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo37)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (37, '2025-01-01T00:00:00.0000000Z', 'Migration_037_DocumentVersionsDocumentIdIndex');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 37, 6);

                    INSERT INTO projects (snapshot_id, name, assembly_name, target_framework)
                    VALUES ('s1', 'App', 'App', 'net10.0');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var runner = new MigrationRunner(dbPath);
            runner.RunMigrations();
            Assert.Equal(MigrationRunner.LatestVersion, runner.GetCurrentSchemaVersion());

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using var colCmd = verify.CreateCommand();
            colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name = 'internals_visible_to';";
            Assert.Equal(1L, (long)colCmd.ExecuteScalar()!);

            using var rowCmd = verify.CreateCommand();
            rowCmd.CommandText = "SELECT internals_visible_to FROM projects WHERE snapshot_id = 's1' AND name = 'App';";
            Assert.Equal(DBNull.Value, rowCmd.ExecuteScalar());

            var facts = ProjectFacts.Load(verify, "s1");
            Assert.False(facts.GrantsInternalsOutsideSnapshot("App"));
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void RoundTrip_Migration046_ProjectFilePath()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v37-projpath-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo37 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 37)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo37)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (37, '2025-01-01T00:00:00.0000000Z', 'Migration_037_DocumentVersionsDocumentIdIndex');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 37, 6);

                    INSERT INTO projects (snapshot_id, name, assembly_name, target_framework)
                    VALUES ('s1', 'App', 'App', 'net10.0');
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var runner = new MigrationRunner(dbPath);
            runner.RunMigrations();
            Assert.Equal(MigrationRunner.LatestVersion, runner.GetCurrentSchemaVersion());

            using (var verify = TestDatabase.OpenReadOnly(dbPath))
            {
                using var colCmd = verify.CreateCommand();
                colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('projects') WHERE name = 'project_path';";
                Assert.Equal(1L, (long)colCmd.ExecuteScalar()!);

                using var rowCmd = verify.CreateCommand();
                rowCmd.CommandText = "SELECT project_path FROM projects WHERE snapshot_id = 's1' AND name = 'App';";
                Assert.Equal(DBNull.Value, rowCmd.ExecuteScalar());
            }

            using var store = new SqliteIndexStore(dbPath);
            store.Open();
            try
            {
                var row = store.LoadSnapshotMetadata("s1");
                Assert.NotNull(row);
                var project = Assert.Single(row!.Projects);
                Assert.Null(project.ProjectPath);
            }
            finally
            {
                store.Close();
            }
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public void RoundTrip_Migration047_SnapshotSymbolBareFqn()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-v37-barefqn-{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = TestDatabase.Open(dbPath))
            {
                using var pragmaCmd = connection.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA foreign_keys = OFF;";
                pragmaCmd.ExecuteNonQuery();

                var migrationsTo37 = typeof(MigrationRunner).Assembly
                    .GetTypes()
                    .Where(static t => !t.IsAbstract && typeof(IMigration).IsAssignableFrom(t))
                    .Select(static t => (IMigration)Activator.CreateInstance(t)!)
                    .Where(static m => m.Version <= 37)
                    .OrderBy(static m => m.Version)
                    .ToList();

                foreach (var migration in migrationsTo37)
                    migration.Up(connection);

                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO schema_metadata (version, applied_at_utc, migration_id)
                    VALUES (37, '2025-01-01T00:00:00.0000000Z', 'Migration_037_DocumentVersionsDocumentIdIndex');

                    INSERT INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('ws1', '/repo', '/repo/Test.slnx');

                    INSERT INTO snapshots (snapshot_id, workspace_id, built_at_utc, sdk_version, database_schema_version, output_schema_version)
                    VALUES ('s1', 'ws1', '2025-01-01T00:00:00.0000000Z', '10.0.100', 37, 6);

                    INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind) VALUES
                        ('M:Ns.Repo`1.Add(`0)|Asm', 'M:Ns.Repo`1.Add(`0)', 'Asm', 'Method'),
                        ('T:Ns.Plain|Asm', 'T:Ns.Plain', 'Asm', 'NamedType'),
                        ('T:Ns.NullFqn|Asm', 'T:Ns.NullFqn', 'Asm', 'NamedType');

                    INSERT INTO snapshot_symbols (snapshot_id, symbol_id, fqn) VALUES
                        ('s1', 'M:Ns.Repo`1.Add(`0)|Asm', 'global::Ns.Repo<T>.Add'),
                        ('s1', 'T:Ns.Plain|Asm', 'global::Ns.Plain'),
                        ('s1', 'T:Ns.NullFqn|Asm', NULL);
                    """;
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            var runner = new MigrationRunner(dbPath);
            runner.RunMigrations();
            Assert.Equal(MigrationRunner.LatestVersion, runner.GetCurrentSchemaVersion());

            using var verify = TestDatabase.OpenReadOnly(dbPath);
            using (var colCmd = verify.CreateCommand())
            {
                colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('snapshot_symbols') WHERE name = 'bare_fqn';";
                Assert.Equal(1L, (long)colCmd.ExecuteScalar()!);
            }

            using (var idxCmd = verify.CreateCommand())
            {
                idxCmd.CommandText = "SELECT name FROM pragma_index_list('snapshot_symbols');";
                var indexes = new HashSet<string>(StringComparer.Ordinal);
                using var reader = idxCmd.ExecuteReader();
                while (reader.Read())
                    indexes.Add(reader.GetString(0));
                Assert.Contains("idx_snapshot_symbols_bare_fqn", indexes);
                Assert.Contains("idx_snapshot_symbols_fqn", indexes);
            }

            var bareFqns = new Dictionary<string, object?>(StringComparer.Ordinal);
            using (var rowCmd = verify.CreateCommand())
            {
                rowCmd.CommandText = "SELECT symbol_id, bare_fqn FROM snapshot_symbols WHERE snapshot_id = 's1';";
                using var reader = rowCmd.ExecuteReader();
                while (reader.Read())
                    bareFqns[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            Assert.Equal("global::Ns.Repo.Add", bareFqns["M:Ns.Repo`1.Add(`0)|Asm"]);
            Assert.Equal("global::Ns.Plain", bareFqns["T:Ns.Plain|Asm"]);
            Assert.Null(bareFqns["T:Ns.NullFqn|Asm"]);
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }
}

public sealed class IndexHandlerSchemaTests : IntegrationTestBase
{
    [Fact]
    public async Task IndexHandler_Run_AgainstFixtureSolution_CompletesWithoutThrowing()
    {
        CreateProject("SchemaProbe", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace SchemaProbe { public class A { public void Foo() {} } }"
        });
        var handlerOut = Path.Combine(TestDir, "handler-out");
        Directory.CreateDirectory(handlerOut);
        var args = new[] { $"--solution={SolutionPath}", $"--output-dir={handlerOut}" };
        var ex = await Record.ExceptionAsync(() => Lurp.Handlers.IndexHandler.Run(args, new CapturingOutputSink(), CancellationToken.None));
        Assert.Null(ex);
        var dbPath = Path.Combine(handlerOut, "index.db");
        Assert.True(File.Exists(dbPath));
        using var store = new SqliteIndexStore(dbPath);
        store.Open();
        try
        {
            var latest = store.GetLatestSnapshotId();
            Assert.NotNull(latest);
            Assert.False(string.IsNullOrWhiteSpace(latest));
        }
        finally
        {
            store.Close();
        }
    }
}