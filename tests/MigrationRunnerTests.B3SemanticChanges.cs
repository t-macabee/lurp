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
    public class B3SemanticChangesTests : IDisposable
    {
        private readonly string _dbPath;

        public B3SemanticChangesTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_b3test_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private static byte[] StringToBytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithDocument(
            SqliteIndexStore store, string snapshotId)
        {
            var sourceBytes = StringToBytes(
                "using System;\n" +
                "namespace TestNs {\n" +
                "    public class Foo {\n" +
                "        public void Bar() { Console.WriteLine(); }\n" +
                "    }\n" +
                "}\n");

            var lineStarts = "[0,14,33,56,107,113]";

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
                    new DocumentVersion(sourceBytes) { DocumentId = "doc-" + snapshotId, FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void Migration_007_IsIdempotent()
        {
            var runner = new MigrationRunner(_dbPath);
            runner.RunMigrations();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

            runner.RunMigrations();
            Assert.Equal(VersionConstants.DatabaseSchemaVersion, runner.GetCurrentSchemaVersion());

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='semantic_changes';";
            var tableExists = cmd.ExecuteScalar();
            Assert.NotNull(tableExists);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_semantic_changes_to_snapshot';";
            var indexExists = cmd.ExecuteScalar();
            Assert.NotNull(indexExists);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name='idx_semantic_changes_from_to';";
            var indexExists2 = cmd.ExecuteScalar();
            Assert.NotNull(indexExists2);
        }

        [Fact]
        public void SaveAndGetSemanticChanges_RoundTrip()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-001";
            var toSnapshotId = "snap-b3-002";

            var changes = new List<SemanticChange>
            {
                new() {
                    ChangeId = "change-1",
                    FromSnapshotId = fromSnapshotId,
                    ToSnapshotId = toSnapshotId,
                    ChangeType = ChangeType.SymbolAdded,
                    SymbolId = "M:Ns.Foo|asm1",
                    DetailJson = "{\"symbol_id\": \"M:Ns.Foo|asm1\"}",
                    CreatedAtUtc = DateTime.UtcNow
                },
                new() {
                    ChangeId = "change-2",
                    FromSnapshotId = fromSnapshotId,
                    ToSnapshotId = toSnapshotId,
                    ChangeType = ChangeType.SymbolRemoved,
                    SymbolId = "M:Ns.Bar|asm1",
                    DetailJson = "{\"symbol_id\": \"M:Ns.Bar|asm1\"}",
                    CreatedAtUtc = DateTime.UtcNow
                },
            };

            store.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes);

            var loaded = store.GetSemanticChanges(fromSnapshotId, toSnapshotId);
            Assert.Equal(2, loaded.Count);

            var changesForSnapshot = store.GetSemanticChangesToSnapshot(toSnapshotId);
            Assert.Equal(["change-1", "change-2"], changesForSnapshot.Select(change => change.ChangeId));

            var change1 = loaded[0];
            Assert.Equal("change-1", change1.ChangeId);
            Assert.Equal(fromSnapshotId, change1.FromSnapshotId);
            Assert.Equal(toSnapshotId, change1.ToSnapshotId);
            Assert.Equal(ChangeType.SymbolAdded, change1.ChangeType);
            Assert.Equal("M:Ns.Foo|asm1", change1.SymbolId);
            Assert.Equal(
                "{\"symbol_id\": \"M:Ns.Foo|asm1\"}",
                change1.DetailJson);

            var change2 = loaded[1];
            Assert.Equal("change-2", change2.ChangeId);
            Assert.Equal(fromSnapshotId, change2.FromSnapshotId);
            Assert.Equal(toSnapshotId, change2.ToSnapshotId);
            Assert.Equal(ChangeType.SymbolRemoved, change2.ChangeType);
            Assert.Equal("M:Ns.Bar|asm1", change2.SymbolId);
            Assert.Equal(
                "{\"symbol_id\": \"M:Ns.Bar|asm1\"}",
                change2.DetailJson);
        }

        [Fact]
        public void GetSemanticChanges_EmptyList_ReturnsEmpty()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-003";
            var toSnapshotId = "snap-b3-004";

            var loaded = store.GetSemanticChanges(fromSnapshotId, toSnapshotId);
            Assert.Empty(loaded);
        }

        [Fact]
        public void SemanticDiffer_SymbolAddedAndRemoved()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-005";
            var toSnapshotId = "snap-b3-006";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var fromSymbols = new List<string>
            {
                "M:Ns.Foo|asm1",
                "M:Ns.Bar|asm1",
            };
            var toSymbols = new List<string>
            {
                "M:Ns.Bar|asm1",
                "M:Ns.Baz|asm1",
            };

            foreach (var symbolId in fromSymbols)
            {
                var decl = MakeDecl(
                    symbolId: symbolId,
                    docCommentId: "M:Ns.Foo",
                    assembly: "asm1",
                    kind: IndexedSymbolKind.Method,
                    docVersionId: "doc-snap-b3-005:hash1",
                    fullS: 0, fullE: 10,
                    sigS: 0, sigE: 5,
                    bodyS: 6, bodyE: 10,
                    nameS: 0, nameE: 5);
                store.SaveDeclarations(fromSnapshotId, [decl]);
            }

            foreach (var symbolId in toSymbols)
            {
                var decl = MakeDecl(
                    symbolId: symbolId,
                    docCommentId: "M:Ns.Baz",
                    assembly: "asm1",
                    kind: IndexedSymbolKind.Method,
                    docVersionId: "doc-snap-b3-006:hash1",
                    fullS: 0, fullE: 10,
                    sigS: 0, sigE: 5,
                    bodyS: 6, bodyE: 10,
                    nameS: 0, nameE: 5);
                store.SaveDeclarations(toSnapshotId, [decl]);
            }

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var symbolAdded = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolAdded);
            Assert.NotNull(symbolAdded);
            Assert.Equal("M:Ns.Baz|asm1", symbolAdded.SymbolId);

            var symbolRemoved = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRemoved);
            Assert.NotNull(symbolRemoved);
            Assert.Equal("M:Ns.Foo|asm1", symbolRemoved.SymbolId);
        }

        [Fact]
        public void SemanticDiffer_EdgeAddedAndRemoved()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-009";
            var toSnapshotId = "snap-b3-010";

            var fromEdges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Ns.Foo|asm1",
                    TargetSymbolId = "M:Ns.Bar|asm1",
                    Kind = "Calls",
                    Provenance = "compiler_proved",
                },
            };
            var toEdges = new List<EdgeRecord>
            {
                new() {
                    SourceSymbolId = "M:Ns.Bar|asm1",
                    TargetSymbolId = "M:Ns.Baz|asm1",
                    Kind = "Calls",
                    Provenance = "compiler_proved",
                },
            };

            store.SaveEdges(fromSnapshotId, fromEdges);
            store.SaveEdges(toSnapshotId, toEdges);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var edgeAdded = changes.FirstOrDefault(c => c.ChangeType == ChangeType.EdgeAdded);
            Assert.NotNull(edgeAdded);
            Assert.Equal("M:Ns.Bar|asm1", edgeAdded.SymbolId);
            Assert.Contains("\"target\":\"M:Ns.Baz|asm1\"", edgeAdded.DetailJson!);

            var edgeRemoved = changes.FirstOrDefault(c => c.ChangeType == ChangeType.EdgeRemoved);
            Assert.NotNull(edgeRemoved);
            Assert.Equal("M:Ns.Foo|asm1", edgeRemoved.SymbolId);
            Assert.Contains("\"target\":\"M:Ns.Bar|asm1\"", edgeRemoved.DetailJson!);
        }

        [Theory]
        [InlineData("possible", null, false, null, ChangeType.EdgeEvidenceChanged)]
        [InlineData("compiler_proved", "[\"Customer\"]", false, null, ChangeType.EdgeEvidenceChanged)]
        [InlineData("compiler_proved", null, true, null, ChangeType.EdgeEvidenceChanged)]
        [InlineData("compiler_proved", null, false, "[[\"T:Ns.IReceiver|asm1\"]]", ChangeType.EdgeEvidenceChanged)]
        [InlineData("compiler_proved", null, false, null, ChangeType.EdgeLocationChanged)]
        public void SemanticDiffer_SameRelationChangedPayload_ReportsSpecificChange(
            string toProvenance,
            string? toTypeArgumentsJson,
            bool toIsCrossGenerated,
            string? toReceiverTypeConstraintsJson,
            string expectedChangeType)
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            const string fromSnapshotId = "snap-edge-payload-from";
            const string toSnapshotId = "snap-edge-payload-to";
            var fromEdge = new EdgeRecord
            {
                SourceSymbolId = "M:Ns.Foo|asm1",
                TargetSymbolId = "M:Ns.Bar|asm1",
                Kind = "Calls",
                Provenance = "compiler_proved",
                SourceDocumentPath = "src/Foo.cs",
                SourceStartLine = 10,
                SourceStartColumn = 4,
                SourceEndLine = 10,
                SourceEndColumn = 9,
            };
            var locationOnly = expectedChangeType == ChangeType.EdgeLocationChanged;
            var toEdge = new EdgeRecord
            {
                SourceSymbolId = fromEdge.SourceSymbolId,
                TargetSymbolId = fromEdge.TargetSymbolId,
                Kind = fromEdge.Kind,
                Provenance = toProvenance,
                TypeArgumentsJson = toTypeArgumentsJson,
                ReceiverTypeConstraintsJson = toReceiverTypeConstraintsJson,
                IsCrossGenerated = toIsCrossGenerated,
                SourceDocumentPath = locationOnly ? "src/MovedFoo.cs" : fromEdge.SourceDocumentPath,
                SourceStartLine = locationOnly ? 12 : fromEdge.SourceStartLine,
                SourceStartColumn = fromEdge.SourceStartColumn,
                SourceEndLine = locationOnly ? 12 : fromEdge.SourceEndLine,
                SourceEndColumn = fromEdge.SourceEndColumn,
            };

            store.SaveEdges(fromSnapshotId, [fromEdge]);
            store.SaveEdges(toSnapshotId, [toEdge]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var change = Assert.Single(changes, c => c.ChangeType == expectedChangeType);
            Assert.Equal(fromEdge.SourceSymbolId, change.SymbolId);
            Assert.DoesNotContain(changes, c => c.ChangeType is ChangeType.EdgeAdded or ChangeType.EdgeRemoved);
            Assert.Contains("\"before\"", change.DetailJson!);
            Assert.Contains("\"after\"", change.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_SignatureChangedViaMetadata()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-011";
            var toSnapshotId = "snap-b3-012";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-011:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Foo()\", \"return_type\": \"void\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-012:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"int Foo()\", \"return_type\": \"int\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
        }

        [Fact]
        public void SemanticDiffer_BaseTypeChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-017";
            var toSnapshotId = "snap-b3-018";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "T:Ns.MyClass|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.MyClass",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-b3-017:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"base_type\": \"global::Ns.BaseClassA\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.MyClass",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-b3-018:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"base_type\": \"global::Ns.BaseClassB\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var baseTypeChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.BaseTypeChanged);
            Assert.NotNull(baseTypeChanged);
            Assert.Equal(symbolId, baseTypeChanged.SymbolId);
        }

        [Fact]
        public void SemanticDiffer_SymbolRenamed()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-013";
            var toSnapshotId = "snap-b3-014";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var fromSymbolId = "M:Ns.OldName|asm1";
            var toSymbolId = "M:Ns.NewName|asm1";

            var fromDecl = MakeDecl(
                symbolId: fromSymbolId,
                docCommentId: "M:Ns.OldName",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-013:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                fqn: "Ns.OldName");

            var toDecl = MakeDecl(
                symbolId: toSymbolId,
                docCommentId: "M:Ns.NewName",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-014:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                fqn: "Ns.NewName");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var symbolRenamed = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRenamed);
            Assert.NotNull(symbolRenamed);
            Assert.Equal(toSymbolId, symbolRenamed.SymbolId);
            Assert.Contains(fromSymbolId, symbolRenamed.DetailJson!);
            Assert.Contains(toSymbolId, symbolRenamed.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_EmptyDiff()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-015";
            var toSnapshotId = "snap-b3-016";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-015:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5);

            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-016:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5);

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            Assert.Empty(changes);
        }

        [Fact]
        public void SemanticDiffer_GenericConstraintChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-019";
            var toSnapshotId = "snap-b3-020";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo|asm1";

            // T constrained only by class
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-019:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Foo<T>(T) where T : class\"}");

            // Same method, but T now also constrained by new()
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-020:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Foo<T>(T) where T : class, new()\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
            Assert.Contains("where T : class", signatureChanged.DetailJson!);
            Assert.Contains("where T : class, new()", signatureChanged.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_ExplicitInterfaceImplementationChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-021";
            var toSnapshotId = "snap-b3-022";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.MyType|asm1";

            // Implicit implementation
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.MyType",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-021:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Dispose()\"}");

            // Explicit interface implementation
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.MyType",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-b3-022:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void IDisposable.Dispose()\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
            Assert.Contains("Dispose()", signatureChanged.DetailJson!);
            Assert.Contains("IDisposable.Dispose()", signatureChanged.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_NullableAnnotationChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-s1-001";
            var toSnapshotId = "snap-s1-002";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo.Bar|asm1";

            // Parameter is non-nullable string
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo.Bar",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s1-001:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"string Bar(string input)\"}");

            // Same method but parameter is now nullable
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo.Bar",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s1-002:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"string Bar(string? input)\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
            Assert.Contains("string input", signatureChanged.DetailJson!);
            Assert.Contains("string? input", signatureChanged.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_RefParameterModifierChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-s2-001";
            var toSnapshotId = "snap-s2-002";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Foo.Baz|asm1";

            // Parameter passed by value
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo.Baz",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s2-001:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Baz(int value)\"}");

            // Same method but parameter is now ref
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Foo.Baz",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s2-002:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"void Baz(ref int value)\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
            Assert.Contains("int value", signatureChanged.DetailJson!);
            Assert.Contains("ref int value", signatureChanged.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_OperatorOverloadSignatureChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-s5-001";
            var toSnapshotId = "snap-s5-002";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Money.op_Addition|asm1";

            // operator +(Money, Money)
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Money.op_Addition",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s5-001:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"Money operator +(Money a, Money b)\"}");

            // Return type changed to decimal
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Money.op_Addition",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s5-002:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"decimal operator +(Money a, Money b)\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
            Assert.Contains("Money operator", signatureChanged.DetailJson!);
            Assert.Contains("decimal operator", signatureChanged.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_ConversionOperatorSignatureChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-s6-001";
            var toSnapshotId = "snap-s6-002";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "M:Ns.Fraction.op_Explicit~System.Int32|asm1";

            // implicit operator
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Fraction.op_Explicit~System.Int32",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s6-001:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"implicit operator int(Fraction f)\"}");

            // changed to explicit
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "M:Ns.Fraction.op_Explicit~System.Int32",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-s6-002:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"signature\": \"explicit operator int(Fraction f)\"}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.NotNull(signatureChanged);
            Assert.Equal(symbolId, signatureChanged.SymbolId);
            Assert.Contains("implicit operator", signatureChanged.DetailJson!);
            Assert.Contains("explicit operator", signatureChanged.DetailJson!);
        }

        [Fact]
        public void SemanticDiffer_AttributeAddedOrRemoved()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-023";
            var toSnapshotId = "snap-b3-024";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "T:Ns.MyClass|asm1";

            // MyClass has [Obsolete]
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.MyClass",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-b3-023:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"attributes\": [\"global::System.ObsoleteAttribute\"]}");

            // MyClass has no attributes
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.MyClass",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-b3-024:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"attributes\": []}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var attributeChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.AttributeChanged);
            Assert.NotNull(attributeChanged);
            Assert.Equal(symbolId, attributeChanged.SymbolId);
        }

        [Fact]
        public void SemanticDiffer_AttributeArgumentChanged()
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            var fromSnapshotId = "snap-b3-025";
            var toSnapshotId = "snap-b3-026";
            CreateSnapshotWithDocument(store, fromSnapshotId);
            CreateSnapshotWithDocument(store, toSnapshotId);

            var symbolId = "T:Ns.MyClass|asm1";

            // MyClass has [Obsolete("v1")]
            var fromDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.MyClass",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-b3-025:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"attributes\": [\"global::System.ObsoleteAttribute(\\\"v1\\\")\"]}");

            // MyClass has [Obsolete("v2")]
            var toDecl = MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.MyClass",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-b3-026:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: "{\"attributes\": [\"global::System.ObsoleteAttribute(\\\"v2\\\")\"]}");

            store.SaveDeclarations(fromSnapshotId, [fromDecl]);
            store.SaveDeclarations(toSnapshotId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

            var attributeChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.AttributeChanged);
            Assert.NotNull(attributeChanged);
            Assert.Equal(symbolId, attributeChanged.SymbolId);
        }
    }
}
