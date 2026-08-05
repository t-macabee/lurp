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
    public class B7ImpactTraverserTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public B7ImpactTraverserTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b7_{Guid.NewGuid():N}.db");
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
        public void TraceImpact_NonExistentSymbol_ReturnsEmpty()
        {
            var store = CreateStoreWithEdges("snap-b7-001", []);
            var traverser = new ImpactTraverser(store, "snap-b7-001");

            var paths = traverser.TraceImpact("nonexistent", ImpactDirection.Downstream);

            Assert.Empty(paths);
            store.Close();
        }

        [Fact]
        public void TraceImpact_SingleHopDownstream_ReturnsOnePath()
        {
            var snapshotId = "snap-b7-002";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "v1", SourceDocumentPath = "src/A.cs", SourceStartLine = 10 }
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            var path = Assert.Single(paths);
            Assert.False(path.Truncated);
            Assert.Null(path.TruncationReason);
            Assert.Equal(1, path.TotalSteps);
            var hop = Assert.Single(path.Hops);
            Assert.Equal("M:A|asm1", hop.SourceSymbolId);
            Assert.Equal("M:B|asm1", hop.TargetSymbolId);
            Assert.Equal("Calls", hop.EdgeKind);
            Assert.Equal("compiler_proved", hop.Provenance);
            Assert.Equal("src/A.cs", hop.SourceDocument);
            Assert.Equal(10, hop.SourceLine);
            store.Close();
        }

        [Fact]
        public void TraceImpact_MultiHopChain_ReturnsOnePathWithTwoHops()
        {
            var snapshotId = "snap-b7-003";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            var path = Assert.Single(paths);
            Assert.Equal(2, path.TotalSteps);
            Assert.False(path.Truncated);
            Assert.Equal("M:A|asm1", path.Hops[0].SourceSymbolId);
            Assert.Equal("M:B|asm1", path.Hops[0].TargetSymbolId);
            Assert.Equal("M:B|asm1", path.Hops[1].SourceSymbolId);
            Assert.Equal("M:C|asm1", path.Hops[1].TargetSymbolId);
            store.Close();
        }

        [Fact]
        public void TraceImpact_Branching_ReturnsTwoPaths()
        {
            var snapshotId = "snap-b7-004";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            Assert.Equal(2, paths.Count);
            Assert.All(paths, p => Assert.Equal(1, p.TotalSteps));
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:B|asm1");
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:C|asm1");
            store.Close();
        }

        [Fact]
        public void TraceImpact_Upstream_ReturnsTwoPaths()
        {
            var snapshotId = "snap-b7-005";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:C|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:B|asm1", ImpactDirection.Upstream);

            Assert.Equal(2, paths.Count);
            Assert.All(paths, p => Assert.Equal(1, p.TotalSteps));
            Assert.Contains(paths, p => p.Hops[0].SourceSymbolId == "M:C|asm1");
            Assert.Contains(paths, p => p.Hops[0].SourceSymbolId == "M:A|asm1");
            Assert.All(paths, p => Assert.Equal("M:B|asm1", p.Hops[0].TargetSymbolId));
            store.Close();
        }

        [Fact]
        public void TraceImpact_CycleDetection_PreventsInfiniteLoop()
        {
            var snapshotId = "snap-b7-006";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B|asm1", TargetSymbolId = "M:A|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream, maxDepth: 5);

            var path = Assert.Single(paths);
            Assert.Equal(1, path.TotalSteps);
            Assert.Equal("M:A|asm1", path.Hops[0].SourceSymbolId);
            Assert.Equal("M:B|asm1", path.Hops[0].TargetSymbolId);
            store.Close();
        }

        [Fact]
        public void TraceImpact_MaxDepthTruncation_ReturnsTruncatedPath()
        {
            var snapshotId = "snap-b7-007";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:B|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream, maxDepth: 1);

            var path = Assert.Single(paths);
            Assert.True(path.Truncated);
            Assert.Equal("max depth reached", path.TruncationReason);
            Assert.Equal(1, path.TotalSteps);
            store.Close();
        }

        [Fact]
        public void TraceImpact_EdgeKindFiltering_OnlyReturnsAllowedKinds()
        {
            var snapshotId = "snap-b7-008";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Reads", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact(
                "M:A|asm1", ImpactDirection.Downstream,
                allowedEdgeKinds: ["Calls"]);

            var path = Assert.Single(paths);
            Assert.Equal("M:B|asm1", path.Hops[0].TargetSymbolId);
            Assert.Equal("Calls", path.Hops[0].EdgeKind);
            store.Close();
        }

        [Fact]
        public void TraceImpact_IncludeSourceFalse_SourceFieldsAreNull()
        {
            var snapshotId = "snap-b7-009";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1", SourceDocumentPath = "src/A.cs", SourceStartLine = 10 }
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream, includeSource: false);

            var path = Assert.Single(paths);
            var hop = Assert.Single(path.Hops);
            Assert.Null(hop.SourceDocument);
            Assert.Null(hop.SourceLine);
            Assert.Equal("M:A|asm1", hop.SourceSymbolId);
            Assert.Equal("M:B|asm1", hop.TargetSymbolId);
            store.Close();
        }

        [Fact]
        public void TraceImpact_EmptyEdgeListForExistingSymbol_ReturnsEmpty()
        {
            var snapshotId = "snap-b7-010";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:B|asm1", ImpactDirection.Downstream);

            Assert.Empty(paths);
            store.Close();
        }

        [Fact]
        public void TraceImpact_LeafNodeNoOutgoingEdges_ReturnsEmpty()
        {
            var snapshotId = "snap-b7-011";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:C|asm1", ImpactDirection.Downstream);

            Assert.Empty(paths);
            store.Close();
        }

        [Fact]
        public void TraceImpact_MultipleEdgeKindsWithFiltering_ReturnsCorrectSubset()
        {
            var snapshotId = "snap-b7-012";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:C|asm1", Kind = "Reads", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:D|asm1", Kind = "Writes", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1" },
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact(
                "M:A|asm1", ImpactDirection.Downstream,
                allowedEdgeKinds: ["Calls", "Reads"]);

            Assert.Equal(2, paths.Count);
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:B|asm1");
            Assert.Contains(paths, p => p.Hops[0].TargetSymbolId == "M:C|asm1");
            Assert.DoesNotContain(paths, p => p.Hops[0].TargetSymbolId == "M:D|asm1");
            store.Close();
        }

        [Fact]
        public void TraceImpact_IncludeSourceDefaultTrue_IncludesSourceFields()
        {
            var snapshotId = "snap-b7-013";
            var edges = new List<EdgeRecord>
            {
                new() { SourceSymbolId = "M:A|asm1", TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "cp", SnapshotId = snapshotId, ExtractorVersion = "v1", SourceDocumentPath = "src/A.cs", SourceStartLine = 42 }
            };
            var store = CreateStoreWithEdges(snapshotId, edges);
            var traverser = new ImpactTraverser(store, snapshotId);

            var paths = traverser.TraceImpact("M:A|asm1", ImpactDirection.Downstream);

            var path = Assert.Single(paths);
            var hop = Assert.Single(path.Hops);
            Assert.Equal("src/A.cs", hop.SourceDocument);
            Assert.Equal(42, hop.SourceLine);
            store.Close();
        }

        [Fact]
        public void TraceImpact_SemanticChanges_ExplainCauseOfDownstreamImpact()
        {
            var snapshotId = "snap-b7-semantic-002";
            var previousSnapshotId = "snap-b7-semantic-001";
            var changedSymbolId = "M:A|asm1";
            var store = CreateStoreWithEdges(snapshotId,
            [
                new() { SourceSymbolId = changedSymbolId, TargetSymbolId = "M:B|asm1", Kind = "Calls", Provenance = "compiler_proved", SnapshotId = snapshotId, ExtractorVersion = "v1" }
            ]);
            store.SaveSemanticChanges(previousSnapshotId, snapshotId,
            [
                new SemanticChange
                {
                    ChangeId = "semantic-change-001",
                    FromSnapshotId = previousSnapshotId,
                    ToSnapshotId = snapshotId,
                    ChangeType = ChangeType.SignatureChanged,
                    SymbolId = changedSymbolId,
                    DetailJson = "{\"before\":\"void A()\",\"after\":\"void A(int value)\"}",
                    CreatedAtUtc = DateTime.UtcNow
                }
            ]);
            var traverser = new ImpactTraverser(store, snapshotId, store);

            var path = Assert.Single(traverser.TraceImpact(changedSymbolId, ImpactDirection.Downstream));

            var cause = Assert.Single(path.SemanticCauses);
            Assert.Equal(ChangeType.SignatureChanged, cause.ChangeType);
            Assert.Equal(changedSymbolId, cause.SymbolId);
            Assert.Equal(previousSnapshotId, cause.FromSnapshotId);
            store.Close();
        }
    }
}
