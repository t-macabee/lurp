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
    public class C16SimulationTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public C16SimulationTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_c16_sim_{Guid.NewGuid():N}.db");
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

        [Fact]
        public void SimulateRename_CallerEdge_ReportsCallerSymbol()
        {
            const string snapId = "snap-c16-sim-001";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Calls",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("M:B|asm", "BRenamed");

            Assert.Equal("rename", report.SimulationType);
            Assert.Contains(report.Items, i => i.SymbolId == "M:A|asm");
            store.Close();
        }

        [Fact]
        public void SimulateRename_OverrideEdge_ReportsOverrideDeclaration()
        {
            const string snapId = "snap-c16-sim-002";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:C|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Overrides",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("M:B|asm", "BRenamed");

            Assert.Contains(report.Items, i => i.SymbolId == "M:C|asm" && i.EdgeKind == "Overrides");
            store.Close();
        }

        [Fact]
        public void SimulateRename_RegistersEdge_ReportsRegistrationSource()
        {
            const string snapId = "snap-c16-sim-007";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Startup|asm",
                    TargetSymbolId = "T:InstrumentTypeService|asm",
                    Kind = "Registers",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("T:InstrumentTypeService|asm", "RenamedService");

            Assert.Contains(report.Items, i => i.SymbolId == "T:Startup|asm" && i.EdgeKind == "Registers");
            store.Close();
        }

        [Fact]
        public void SimulateRename_NoCallers_ReturnsEmptyItems()
        {
            const string snapId = "snap-c16-sim-003";
            var store = CreateStoreWithEdges(snapId, []);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("M:B|asm", "BRenamed");

            Assert.Empty(report.Items);
            Assert.Equal(0, report.AffectedCount);
            store.Close();
        }

        [Fact]
        public void SimulateRename_StaticTypeWithNoDirectEdges_ReportsMemberCallers()
        {
            // A static class is never referenced by a Calls/References/etc.
            // edge on the type node itself: callers target its static
            // members, not the type. Renaming the type still touches every
            // one of those qualified call sites (e.g. "Runner.Go()"), so the
            // simulation must walk the type's Declares edges to find them.
            const string snapId = "snap-c16-sim-008";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Runner|asm",
                    TargetSymbolId = "M:Runner.Go|asm",
                    Kind = "Declares",
                },
                new() {
                    SourceSymbolId = "M:Caller.Invoke|asm",
                    TargetSymbolId = "M:Runner.Go|asm",
                    Kind = "Calls",
                },
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRename("T:Runner|asm", "RunnerRenamed");

            Assert.Contains(report.Items, i => i.SymbolId == "M:Caller.Invoke|asm" && i.EdgeKind == "Calls");
            Assert.Equal(1, report.AffectedCount);
            store.Close();
        }

        [Fact]
        public void SimulateMove_CallerWithDocumentPath_ReportsDocumentPath()
        {
            const string snapId = "snap-c16-sim-004";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Calls",
                    Provenance = "compiler_proved",
                    SnapshotId = snapId,
                    ExtractorVersion = "v1",
                    SourceDocumentPath = "src/A.cs",
                    SourceStartLine = 10,
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateMove("M:B|asm", "NewNs");

            var item = Assert.Single(report.Items);
            Assert.Equal("src/A.cs", item.DocumentPath);
            Assert.Equal(10, item.Line);
            store.Close();
        }

        [Fact]
        public void SimulateMove_StaticTypeWithNoDirectEdges_ReportsMemberCallers()
        {
            // Regression: a static type is never referenced by a
            // Calls/References edge on the type node itself; callers target
            // its static members. SimulateMove previously queried only the
            // type's incoming edges and missed them.
            const string snapId = "snap-c16-sim-009";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Runner|asm",
                    TargetSymbolId = "M:Runner.Go|asm",
                    Kind = "Declares",
                },
                new() {
                    SourceSymbolId = "M:Caller.Invoke|asm",
                    TargetSymbolId = "M:Runner.Go|asm",
                    Kind = "Calls",
                },
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateMove("T:Runner|asm", "New.Ns");

            Assert.Contains(report.Items, i => i.SymbolId == "M:Caller.Invoke|asm" && i.EdgeKind == "Calls");
            store.Close();
        }

        [Fact]
        public void SimulateMove_OverrideEdge_ReportsOverrideDeclaration()
        {
            // Regression: SimulateMove's filter omitted "Overrides".
            const string snapId = "snap-c16-sim-010";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:C|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Overrides",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateMove("M:B|asm", "New.Ns");

            Assert.Contains(report.Items, i => i.SymbolId == "M:C|asm" && i.EdgeKind == "Overrides");
            store.Close();
        }

        [Fact]
        public void SimulateMove_RegistersEdge_ReportsRegistrationSource()
        {
            // Regression: SimulateMove's filter omitted "Registers".
            const string snapId = "snap-c16-sim-011";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Startup|asm",
                    TargetSymbolId = "T:InstrumentTypeService|asm",
                    Kind = "Registers",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateMove("T:InstrumentTypeService|asm", "New.Ns");

            Assert.Contains(report.Items, i => i.SymbolId == "T:Startup|asm" && i.EdgeKind == "Registers");
            store.Close();
        }

        [Fact]
        public void SimulateRemove_DependentWithRegistration_ReportsOrphanedRegistration()
        {
            const string snapId = "snap-c16-sim-005";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "T:Startup|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Registers",
                },
                new() {
                    SourceSymbolId = "M:A|asm",
                    TargetSymbolId = "M:B|asm",
                    Kind = "Calls",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRemove("M:B|asm");

            Assert.Contains(report.Items, i => i.EdgeKind == "Registers");
            store.Close();
        }

        [Fact]
        public void SimulateRemove_SymbolWithTest_ReportsOrphanedTest()
        {
            const string snapId = "snap-c16-sim-006";
            var edges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:B|asm",
                    TargetSymbolId = "M:FooTest|asm",
                    Kind = "TestedBy",
                }
            };
            var store = CreateStoreWithEdges(snapId, edges);
            var engine = new SimulationEngine(store, store, snapId);

            var report = engine.SimulateRemove("M:B|asm");

            Assert.Contains(report.Items, i => i.EdgeKind == "TestedBy");
            store.Close();
        }
    }
}
