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
    public class B0ExpansionTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public B0ExpansionTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b0_{Guid.NewGuid():N}.db");
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

        [Fact]
        public void Migration006_RunTwice_IsIdempotent()
        {
            var runner = new MigrationRunner(_dbPath);

            runner.RunMigrations();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

            runner.RunMigrations();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());
        }

        [Fact]
        public void SaveAndGetEdge_WithAllNewFields_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-rt-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:Ns.Foo.Bar|asm1", TargetSymbolId = "M:Ns.Baz.Qux|asm1", Kind = "Calls", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "member-edges-v1", SourceDocumentPath = "src/Foo.cs", SourceStartLine = 42, SourceStartColumn = 13, SourceEndLine = 42, SourceEndColumn = 30 }
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            var edge = Assert.Single(loaded);

            Assert.Equal("M:Ns.Foo.Bar|asm1", edge.SourceSymbolId);
            Assert.Equal("M:Ns.Baz.Qux|asm1", edge.TargetSymbolId);
            Assert.Equal("Calls", edge.Kind);
            Assert.Equal("compiler_proved", edge.Provenance);
            Assert.Equal(snapshotId, edge.SnapshotId);
            Assert.Equal("member-edges-v1", edge.ExtractorVersion);
            Assert.Equal("src/Foo.cs", edge.SourceDocumentPath);
            Assert.Equal(42, edge.SourceStartLine);
            Assert.Equal(13, edge.SourceStartColumn);
            Assert.Equal(42, edge.SourceEndLine);
            Assert.Equal(30, edge.SourceEndColumn);

            store.Close();
        }

        [Fact]
        public void SaveAndGetEdge_WithNullLocationFields_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-rt-002";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Bar|asm1", Kind = "Inherits", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "v1" }
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            var edge = Assert.Single(loaded);

            Assert.Equal("Inherits", edge.Kind);
            Assert.Equal("compiler_proved", edge.Provenance);
            Assert.Equal(snapshotId, edge.SnapshotId);
            Assert.Equal("v1", edge.ExtractorVersion);
            Assert.Null(edge.SourceDocumentPath);
            Assert.Null(edge.SourceStartLine);
            Assert.Null(edge.SourceStartColumn);
            Assert.Null(edge.SourceEndLine);
            Assert.Null(edge.SourceEndColumn);

            store.Close();
        }

        [Fact]
        public void SaveAndGetEdge_BackwardCompatibleConstructor_StillWorks()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-bc-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Bar|asm1", Kind = "Inherits", Provenance = "roslyn" },
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.IBaz|asm1", Kind = "Implements" },
            };

            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            Assert.Equal(2, loaded.Count);

            Assert.Equal("T:Ns.Foo|asm1", loaded[0].SourceSymbolId);
            Assert.Equal("T:Ns.Bar|asm1", loaded[0].TargetSymbolId);
            Assert.Equal("Inherits", loaded[0].Kind);
            Assert.Equal("roslyn", loaded[0].Provenance);

            Assert.Equal(snapshotId, loaded[0].SnapshotId);
            Assert.Equal("", loaded[0].ExtractorVersion);
            Assert.Null(loaded[0].SourceDocumentPath);

            Assert.Equal("Implements", loaded[1].Kind);
            Assert.Equal("", loaded[1].Provenance);

            var filtered = store.GetEdges(snapshotId, "T:Ns.Bar|asm1");
            Assert.Single(filtered);

            store.Close();
        }

        [Fact]
        public void GetEdgesByKind_FiltersCorrectly()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-kind-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:A|asm1", TargetSymbolId = "T:Base|asm1", Kind = "Inherits", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "T:A|asm1", TargetSymbolId = "T:IFoo|asm1", Kind = "Implements", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            store.SaveEdges(snapshotId, edges);

            var inherits = store.GetEdgesByKind(snapshotId, "Inherits");
            Assert.Single(inherits);

            var calls = store.GetEdgesByKind(snapshotId, "Calls");
            Assert.Single(calls);

            var nonExistent = store.GetEdgesByKind(snapshotId, "RoutesTo");
            Assert.Empty(nonExistent);

            store.Close();
        }

        [Fact]
        public void GetIncomingEdges_ReturnsEdgesTargetingSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-in-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:C.Qux|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B.Bar|asm1", TargetSymbolId = "M:D.Other|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            store.SaveEdges(snapshotId, edges);

            var incoming = store.GetIncomingEdges(snapshotId, "M:B.Bar|asm1");
            Assert.Equal(2, incoming.Count);
            Assert.All(incoming, e => Assert.Equal("M:B.Bar|asm1", e.TargetSymbolId));

            store.Close();
        }

        [Fact]
        public void GetOutgoingEdges_ReturnsEdgesFromSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-b0-out-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:B.Bar|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A.Foo|asm1", TargetSymbolId = "M:C.Qux|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B.Bar|asm1", TargetSymbolId = "M:D.Other|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            store.SaveEdges(snapshotId, edges);

            var outgoing = store.GetOutgoingEdges(snapshotId, "M:A.Foo|asm1");
            Assert.Equal(2, outgoing.Count);
            Assert.All(outgoing, e => Assert.Equal("M:A.Foo|asm1", e.SourceSymbolId));

            store.Close();
        }
    }
}
