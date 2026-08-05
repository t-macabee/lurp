using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public partial class MigrationRunnerTests
{
    public class SnapshotPrunerAtomicCleanupTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public SnapshotPrunerAtomicCleanupTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_pruner_atomic_{Guid.NewGuid():N}.db");
        }

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
            var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();
            _store = store;
            return store;
        }

        private static void CreateSnapshotWithDeclarations(SqliteIndexStore store, string snapshotId)
        {
            var sourceBytes = Encoding.UTF8.GetBytes(
                "namespace TestNs {\n    public class Foo {\n        public void Bar() { }\n    }\n}\n");
            var lineStarts = "[0,19,42,68,74]";

            var manifest = new SnapshotRow
            {
                SnapshotId = snapshotId,
                WorkspaceId = "workspace:///root/proj",
                GitRoot = "/root",
                SolutionPath = "/root/proj",
                SdkVersion = "10.0.301",
                CompilerVersion = "4.12.0.0",
                CreatedAtUtc = DateTime.UtcNow,
                Documents = new List<DocumentVersion>
                {
                    new DocumentVersion(sourceBytes)
                    {
                        DocumentId = "doc1", FilePath = "src/Foo.cs",
                        ContentHash = "hash1", Encoding = "utf-8",
                        LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue,
                        LineStarts = lineStarts
                    },
                }
            };
            store.SaveSnapshot(manifest);

            var decl = new SymbolDeclaration
            {
                SymbolId = new SymbolId("T:TestNs.Foo", "assembly1", "TestNs.Foo"),
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc1:hash1",
                FullSpan = new DeclarationSpan(18, 73),
                SignatureSpan = new DeclarationSpan(18, 40),
                BodySpan = new DeclarationSpan(40, 73),
                NameSpan = new DeclarationSpan(33, 36),
            };
            store.SaveDeclarations(snapshotId, [decl]);
        }

        private static int CountRows(SqliteConnection conn, string table, string? where = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = where != null
                ? $"SELECT COUNT(*) FROM {table} WHERE {where};"
                : $"SELECT COUNT(*) FROM {table};";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        [Fact]
        public void DeleteSnapshotData_FailedDelete_RollsBackAllTables()
        {
            var store = CreateStore();
            var snapshotId = "snap-atomic-001";
            CreateSnapshotWithDeclarations(store, snapshotId);

            // Insert edges and diagnostics directly via a second connection
            using var rawConn = new SqliteConnection($"Data Source={_dbPath}");
            rawConn.Open();

            using (var insertEdge = rawConn.CreateCommand())
            {
                insertEdge.CommandText = @"
                    INSERT INTO edges (snapshot_id, source_symbol_id, target_symbol_id, kind, provenance)
                    VALUES (@sid, 'T:TestNs.Foo|assembly1', 'M:TestNs.Foo.Bar|assembly1', 'Calls', 'test');";
                insertEdge.Parameters.AddWithValue("@sid", snapshotId);
                insertEdge.ExecuteNonQuery();
            }

            using (var insertDiag = rawConn.CreateCommand())
            {
                insertDiag.CommandText = @"
                    INSERT INTO diagnostics (snapshot_id, project_name, severity, id, message)
                    VALUES (@sid, 'TestProject', 'Warning', 'CS0168', 'Unused variable');";
                insertDiag.Parameters.AddWithValue("@sid", snapshotId);
                insertDiag.ExecuteNonQuery();
            }

            // Record pre-failure counts
            int edgesBefore = CountRows(rawConn, "edges", $"snapshot_id = '{snapshotId}'");
            int diagnosticsBefore = CountRows(rawConn, "diagnostics", $"snapshot_id = '{snapshotId}'");
            int snapshotSymbolsBefore = CountRows(rawConn, "snapshot_symbols", $"snapshot_id = '{snapshotId}'");
            int snapshotsBefore = CountRows(rawConn, "snapshots", $"snapshot_id = '{snapshotId}'");

            Assert.True(edgesBefore > 0, "Precondition: edges must exist before failed delete");
            Assert.True(diagnosticsBefore > 0, "Precondition: diagnostics must exist before failed delete");
            Assert.True(snapshotSymbolsBefore > 0, "Precondition: snapshot_symbols must exist before failed delete");
            Assert.Equal(1, snapshotsBefore);

            // Create a trigger that causes DELETE on snapshot_symbols to fail.
            // snapshot_symbols is the 4th table in the delete list (after edges,
            // diagnostics, annotations), so without a transaction those earlier
            // tables would lose data before the failure.
            using (var createTrigger = rawConn.CreateCommand())
            {
                createTrigger.CommandText = @"
                    CREATE TRIGGER IF NOT EXISTS test_fail_snapshot_symbols_delete
                    BEFORE DELETE ON snapshot_symbols
                    BEGIN
                        SELECT RAISE(ABORT, 'simulated failure');
                    END;";
                createTrigger.ExecuteNonQuery();
            }

            rawConn.Close();

            // Act: the delete must throw
            var ex = Assert.Throws<SqliteException>(() => store.DeleteSnapshotData(snapshotId));
            Assert.Contains("simulated failure", ex.Message);

            // Assert: all data must still be intact (transaction rolled back)
            using var verifyConn = new SqliteConnection($"Data Source={_dbPath}");
            verifyConn.Open();

            Assert.Equal(edgesBefore, CountRows(verifyConn, "edges", $"snapshot_id = '{snapshotId}'"));
            Assert.Equal(diagnosticsBefore, CountRows(verifyConn, "diagnostics", $"snapshot_id = '{snapshotId}'"));
            Assert.Equal(snapshotSymbolsBefore, CountRows(verifyConn, "snapshot_symbols", $"snapshot_id = '{snapshotId}'"));
            Assert.Equal(snapshotsBefore, CountRows(verifyConn, "snapshots", $"snapshot_id = '{snapshotId}'"));

            // Cleanup: drop the trigger and verify a normal delete succeeds
            using (var dropTrigger = verifyConn.CreateCommand())
            {
                dropTrigger.CommandText = "DROP TRIGGER IF EXISTS test_fail_snapshot_symbols_delete;";
                dropTrigger.ExecuteNonQuery();
            }
            verifyConn.Close();

            store.DeleteSnapshotData(snapshotId);

            using var finalConn = new SqliteConnection($"Data Source={_dbPath}");
            finalConn.Open();
            Assert.Equal(0, CountRows(finalConn, "edges", $"snapshot_id = '{snapshotId}'"));
            Assert.Equal(0, CountRows(finalConn, "snapshots", $"snapshot_id = '{snapshotId}'"));
            finalConn.Close();

            store.Close();
        }
    }
}
