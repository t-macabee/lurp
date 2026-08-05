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
    public class SymbolStoreTests : IDisposable
    {
        private readonly string _dbPath;
        private SqliteIndexStore? _store;

        public SymbolStoreTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_symtest_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            _store?.Dispose();
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

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

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
                    new DocumentVersion(sourceBytes) { DocumentId = "doc1", FilePath = "src/Foo.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        [Fact]
        public void SaveDeclarations_And_GetSymbolInfo_MetadataOnly()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-001";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "T:TestNs.Foo|assembly1",
                docCommentId: "T:TestNs.Foo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Type,
                docVersionId: "doc1:hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, [decl]);

            var info = store.GetSymbolInfo("T:TestNs.Foo|assembly1", snapshotId);
            Assert.NotNull(info);
            Assert.Equal("T:TestNs.Foo", info!.SymbolId.DocCommentId);
            Assert.Equal("assembly1", info.SymbolId.AssemblyIdentity);
            Assert.Equal(IndexedSymbolKind.Type, info.Kind);
            Assert.Equal(1, info.DeclarationCount);
            Assert.False(info.IsPartial);
        }

        [Fact]
        public void SaveDeclarations_And_GetSymbolSource_Body()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-002";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.Bar|assembly1",
                docCommentId: "M:TestNs.Foo.Bar",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc1:hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, [decl]);

            var body = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Body);
            Assert.NotNull(body);
            Assert.Equal("{ Console.WriteLine(); }", body);
        }

        [Fact]
        public void SaveDeclarations_And_GetSymbolSource_FullDeclaration()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-003";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "T:TestNs.Foo|assembly1",
                docCommentId: "T:TestNs.Foo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Type,
                docVersionId: "doc1:hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, [decl]);

            var full = store.GetSymbolSource("T:TestNs.Foo|assembly1", snapshotId, ViewKind.Declaration);
            var expected = "    public class Foo {\n        public void Bar() { Console.WriteLine(); }\n    }";
            Assert.Equal(expected, full);
        }

        [Fact]
        public void GetContainingTypeSource_MethodWithFullyQualifiedParameter_ReturnsContainingType()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-containing-qualified-parameter";
            const string methodId = "M:TestNs.Foo.Bar(TestNs.Models.Argument)|assembly1";
            CreateSnapshotWithDocument(store, snapshotId);

            var type = MakeDecl(
                symbolId: "T:TestNs.Foo|assembly1",
                docCommentId: "T:TestNs.Foo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Type,
                docVersionId: "doc1:hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 54,
                bodyS: 54, bodyE: 112,
                nameS: 50, nameE: 53);
            var method = MakeDecl(
                symbolId: methodId,
                docCommentId: "M:TestNs.Foo.Bar(TestNs.Models.Argument)",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc1:hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);
            store.SaveDeclarations(snapshotId, [type, method]);

            var source = store.GetContainingTypeSource(methodId, snapshotId);

            Assert.NotNull(source);
            Assert.Contains("public class Foo", source, StringComparison.Ordinal);
        }

        [Fact]
        public void GetSymbolInfo_SymbolNotFound_ReturnsNull()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-004";
            CreateSnapshotWithDocument(store, snapshotId);

            var info = store.GetSymbolInfo("T:Nonexistent|assembly1", snapshotId);
            Assert.Null(info);
        }

        [Fact]
        public void GetSymbolSource_NonExistentBodySpan_ReturnsNull()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-005";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.AbstractFoo|assembly1",
                docCommentId: "M:TestNs.Foo.AbstractFoo",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc1:hash1",
                fullS: 33, fullE: 112,
                sigS: 33, sigE: 113,
                bodyS: null, bodyE: null,
                nameS: 50, nameE: 53);

            store.SaveDeclarations(snapshotId, [decl]);

            var body = store.GetSymbolSource("M:TestNs.Foo.AbstractFoo|assembly1", snapshotId, ViewKind.Body);
            Assert.Null(body);
        }

        [Fact]
        public void PartialType_TwoDeclarations_BothLinkedToOneSymbol()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-006";

            var source1 = StringToBytes("partial class Foo { void A() {} }\n");
            var source2 = StringToBytes("partial class Foo { void B() {} }\n");
            var lineStarts = "[0,30]";

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
                    new DocumentVersion(source1) { DocumentId = "doc-part1", FilePath = "src/part1.cs", ContentHash = "hash-p1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                    new DocumentVersion(source2) { DocumentId = "doc-part2", FilePath = "src/part2.cs", ContentHash = "hash-p2", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);

            var symId = new SymbolId("T:Foo", "assembly1", "TestNs.Foo");

            var decl1 = new SymbolDeclaration
            {
                SymbolId = symId,
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-part1:hash-p1",
                FullSpan = new DeclarationSpan(0, 29),
                SignatureSpan = new DeclarationSpan(0, 15),
                BodySpan = new DeclarationSpan(15, 28),
                NameSpan = new DeclarationSpan(15, 18),
                IsPartial = true
            };

            var decl2 = new SymbolDeclaration
            {
                SymbolId = symId,
                Kind = IndexedSymbolKind.Type,
                DocumentVersionId = "doc-part2:hash-p2",
                FullSpan = new DeclarationSpan(0, 29),
                SignatureSpan = new DeclarationSpan(0, 15),
                BodySpan = new DeclarationSpan(15, 28),
                NameSpan = new DeclarationSpan(15, 18),
                IsPartial = true
            };

            store.SaveDeclarations(snapshotId, [decl1, decl2]);

            var info = store.GetSymbolInfo(symId.Value, snapshotId);
            Assert.NotNull(info);
            Assert.Equal(2, info!.DeclarationCount);
            Assert.True(info.IsPartial);

            var body1 = store.GetSymbolSource(symId.Value, snapshotId, ViewKind.Body);
            Assert.NotNull(body1);

            var body2 = store.GetSymbolSource(symId.Value, snapshotId, ViewKind.Body);
            Assert.NotNull(body2);
        }

        [Fact]
        public void SymbolSource_RoundTripSignature()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-007";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.Bar|assembly1",
                docCommentId: "M:TestNs.Foo.Bar",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc1:hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, [decl]);

            var sig = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Signature);
            Assert.Equal("        public void Bar() ", sig);
        }

        [Fact]
        public void SymbolSource_RoundTripName()
        {
            var store = CreateStore();
            var snapshotId = "snap-sym-008";
            CreateSnapshotWithDocument(store, snapshotId);

            var decl = MakeDecl(
                symbolId: "M:TestNs.Foo.Bar|assembly1",
                docCommentId: "M:TestNs.Foo.Bar",
                assembly: "assembly1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc1:hash1",
                fullS: 56, fullE: 107,
                sigS: 56, sigE: 82,
                bodyS: 82, bodyE: 106,
                nameS: 76, nameE: 79);

            store.SaveDeclarations(snapshotId, [decl]);

            var name = store.GetSymbolSource("M:TestNs.Foo.Bar|assembly1", snapshotId, ViewKind.Name);
            Assert.Equal("Bar", name);
        }
    }
}
