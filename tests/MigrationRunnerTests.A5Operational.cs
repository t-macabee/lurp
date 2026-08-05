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
    public class A5OperationalTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public A5OperationalTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_a5_{Guid.NewGuid():N}.db");
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
        public void SaveAndGetEdges_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-edges-001";

            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.Bar|asm1", Kind = "Inherits", Provenance = "roslyn" },
                new() { SourceSymbolId = "T:Ns.Foo|asm1", TargetSymbolId = "T:Ns.IBaz|asm1", Kind = "Implements", Provenance = "roslyn" },
            };
            store.SaveEdges(snapshotId, edges);

            var loaded = store.GetEdges(snapshotId);
            Assert.Equal(2, loaded.Count);
            Assert.Equal("T:Ns.Foo|asm1", loaded[0].SourceSymbolId);
            Assert.Equal("T:Ns.Bar|asm1", loaded[0].TargetSymbolId);
            Assert.Equal("Inherits", loaded[0].Kind);

            var filtered = store.GetEdges(snapshotId, "T:Ns.Bar|asm1");
            Assert.Single(filtered);

            store.Close();
        }

        [Fact]
        public void SaveAndGetDiagnostics_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-diag-001";

            var diagnostics = new List<DiagnosticRecord>
            {
                new() { ProjectName = "MyProject", DocumentPath = "src/Program.cs", Severity = "Warning", Id = "CS0219", Message = "Variable 'x' is unused", StartLine = 10, StartColumn = 5, EndLine = 10, EndColumn = 6 },
                new() { ProjectName = "MyProject", DocumentPath = "src/Utils.cs", Severity = "Error", Id = "CS0103", Message = "The name 'foo' does not exist", StartLine = 5, StartColumn = 1, EndLine = 5, EndColumn = 4 },
            };
            store.SaveDiagnostics(snapshotId, diagnostics);

            var loaded = store.GetDiagnostics(snapshotId);
            Assert.Equal(2, loaded.Count);

            var filtered = store.GetDiagnostics(snapshotId, "MyProject");
            Assert.Equal(2, filtered.Count);

            var noMatch = store.GetDiagnostics(snapshotId, "OtherProject");
            Assert.Empty(noMatch);

            store.Close();
        }

        [Fact]
        public void SaveAndGetAnnotations_RoundTrips()
        {
            var store = CreateStore();
            var snapshotId = "snap-a5-ann-001";

            var annotations = new List<AnnotationRecord>
            {
                new("T:Ns.Foo|asm1", "obsolete", "Use Bar instead"),
                new("M:Ns.Foo.Bar|asm1", "returns", "A result object"),
            };
            store.SaveAnnotations(snapshotId, annotations);

            var loaded = store.GetAnnotations(snapshotId);
            Assert.Equal(2, loaded.Count);

            var filtered = store.GetAnnotations(snapshotId, "T:Ns.Foo|asm1");
            Assert.Single(filtered);
            Assert.Equal("obsolete", filtered[0].Kind);

            store.Close();
        }

        [Fact]
        public void SaveEdges_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveEdges("snap-empty", []);
            Assert.Empty(store.GetEdges("snap-empty"));
            store.Close();
        }

        [Fact]
        public void SaveDiagnostics_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveDiagnostics("snap-empty", []);
            Assert.Empty(store.GetDiagnostics("snap-empty"));
            store.Close();
        }

        [Fact]
        public void SaveAnnotations_EmptyList_DoesNotThrow()
        {
            var store = CreateStore();
            store.SaveAnnotations("snap-empty", []);
            Assert.Empty(store.GetAnnotations("snap-empty"));
            store.Close();
        }
    }
}
