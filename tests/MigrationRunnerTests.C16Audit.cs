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
    public class C16AuditTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public C16AuditTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_c16_aud_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private SqliteIndexStore CreateStoreWithEdges(string snapshotId, List<EdgeRecord> edges)
        {
            _store?.Dispose();
            var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();
            store.SaveEdges(snapshotId, edges);
            _store = store;
            return store;
        }

        private void CreateStoreWithSymbols(SqliteIndexStore store, string snapshotId, List<string> symbolIds, string? metadataJson = null)
        {
            // Seed FK references needed by SaveDeclarations
            using (var fkConn = new SqliteConnection($"Data Source={_dbPath}"))
            {
                fkConn.Open();
                using var fkCmd = fkConn.CreateCommand();
                fkCmd.CommandText = @"
                    INSERT OR IGNORE INTO workspaces (workspace_id, git_root, solution_path)
                    VALUES ('test-ws', '/fake/root', 'test.sln');
                    INSERT OR IGNORE INTO snapshots (snapshot_id, workspace_id, built_at_utc)
                    VALUES (@sid, 'test-ws', '2026-01-01T00:00:00Z');
                    INSERT OR IGNORE INTO documents (document_id, relative_path)
                    VALUES ('doc-1', 'test.cs');
                    INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash)
                    VALUES ('doc-v1', 'doc-1', 'hash');
                ";
                fkCmd.Parameters.AddWithValue("@sid", snapshotId);
                fkCmd.ExecuteNonQuery();
            }

            var declarations = symbolIds.Select(id =>
            {
                var sid = SymbolId.Parse(id);
                return new SymbolDeclaration
                {
                    SymbolId = sid,
                    Kind = IndexedSymbolKind.Method,
                    DocumentVersionId = "doc-v1",
                    FullSpan = new DeclarationSpan(null, null),
                    SignatureSpan = new DeclarationSpan(null, null),
                    BodySpan = new DeclarationSpan(null, null),
                    NameSpan = new DeclarationSpan(null, null),
                    MetadataJson = metadataJson ?? "{\"accessibility\":\"Public\"}"
                };
            }).ToList();
            store.SaveDeclarations(snapshotId, declarations);
        }

        [Fact]
        public void DeadSymbol_NoIncomingEdges_Flagged()
        {
            const string snapId = "snap-c16-aud-001";
            var store = CreateStoreWithEdges(snapId, []);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["dead-symbol"]));

            Assert.Contains(report.Findings, f => f.Check == "dead-symbol" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void DeadSymbol_OnlyTestedByIncoming_StillFlagged()
        {
            const string snapId = "snap-c16-aud-002";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:Test|asm",
                    Kind = "TestedBy",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm", "M:Test|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["dead-symbol"]));

            Assert.Contains(report.Findings, f => f.Check == "dead-symbol" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void DeadSymbol_HasCallsIncoming_NotFlagged()
        {
            const string snapId = "snap-c16-aud-003";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Caller|asm",
                    TargetSymbolId = "M:A|asm",
                    Kind = "Calls",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm", "M:Caller|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["dead-symbol"]));

            Assert.DoesNotContain(report.Findings, f => f.Check == "dead-symbol" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void UntestedSurface_SymbolWithNoTestedBy_Flagged()
        {
            const string snapId = "snap-c16-aud-004";
            var store = CreateStoreWithEdges(snapId, []);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm"],
                metadataJson: "{\"accessibility\":\"Public\"}");
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["untested-surface"]));

            Assert.Contains(report.Findings, f => f.Check == "untested-surface" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void UntestedSurface_SymbolWithTestedByEdge_NotFlagged()
        {
            const string snapId = "snap-c16-aud-005";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:Test|asm",
                    Kind = "TestedBy",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm", "M:Test|asm"],
                metadataJson: "{\"accessibility\":\"Public\"}");
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["untested-surface"]));

            Assert.DoesNotContain(report.Findings, f => f.Check == "untested-surface" && f.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void UnregisteredImpl_ImplementsWithoutRegisters_Flagged()
        {
            const string snapId = "snap-c16-aud-006";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Impl|asm",
                    TargetSymbolId = "T:IFoo|asm",
                    Kind = "Implements",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["T:Impl|asm", "T:IFoo|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["unregistered-impl"]));

            Assert.Contains(report.Findings, f => f.Check == "unregistered-impl" && f.SymbolId == "T:Impl|asm");
            store.Close();
        }

        [Fact]
        public void UnregisteredImpl_ImplementsWithRegisters_NotFlagged()
        {
            const string snapId = "snap-c16-aud-007";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Impl|asm",
                    TargetSymbolId = "T:IFoo|asm",
                    Kind = "Implements",
                },
                new() {
                    SourceSymbolId = "T:Startup|asm",
                    TargetSymbolId = "T:Impl|asm",
                    Kind = "Registers",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["T:Impl|asm", "T:IFoo|asm", "T:Startup|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["unregistered-impl"]));

            Assert.DoesNotContain(report.Findings, f => f.Check == "unregistered-impl" && f.SymbolId == "T:Impl|asm");
            store.Close();
        }

        [Fact]
        public void HighFanOut_ExceedsThreshold_Flagged()
        {
            const string snapId = "snap-c16-aud-008";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T1|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T2|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T3|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T4|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:God|asm",
                    TargetSymbolId = "M:T5|asm",
                    Kind = "Calls",
                },
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:God|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["high-fan-out"], fanOutThreshold: 3));

            Assert.Contains(report.Findings, f => f.Check == "high-fan-out" && f.SymbolId == "M:God|asm");
            store.Close();
        }

        [Fact]
        public void HighFanOut_BelowThreshold_NotFlagged()
        {
            const string snapId = "snap-c16-aud-009";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Lean|asm",
                    TargetSymbolId = "M:T1|asm",
                    Kind = "Calls",
                },
                new() {
                    SourceSymbolId = "M:Lean|asm",
                    TargetSymbolId = "M:T2|asm",
                    Kind = "Calls",
                },
            };
            var store = CreateStoreWithEdges(snapId, edges);
            CreateStoreWithSymbols(store, snapId, ["M:Lean|asm"]);
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["high-fan-out"], fanOutThreshold: 3));

            Assert.DoesNotContain(report.Findings, f => f.Check == "high-fan-out" && f.SymbolId == "M:Lean|asm");
            store.Close();
        }

        [Theory]
        [InlineData("Public")]
        [InlineData("Internal")]
        public void RunAudit_RoslynAccessibilityCasing_FlagsUntestedSurface(string accessibility)
        {
            const string snapId = "snap-c16-aud-010";
            var store = CreateStoreWithEdges(snapId, []);
            CreateStoreWithSymbols(store, snapId, ["M:A|asm"],
                metadataJson: $"{{\"accessibility\":\"{accessibility}\"}}");
            var engine = new AuditEngine(store, snapId);

            var report = engine.RunAudit(new AuditOptions(["untested-surface"]));

            Assert.Contains(report.Findings, f => f.Check == "untested-surface" && f.SymbolId == "M:A|asm");
            store.Close();
        }
    }
}
