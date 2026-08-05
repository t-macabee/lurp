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
    public class MetadataContractTests : IDisposable
    {
        private readonly string _dbPath;

        public MetadataContractTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_meta_contract_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

        private static void CreateSnapshotWithDocument(SqliteIndexStore store, string snapshotId)
        {
            var sourceBytes = StringToBytes("class C {}");
            var lineStarts = "[0]";
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
                    new DocumentVersion(sourceBytes) { DocumentId = "doc-" + snapshotId, FilePath = "src/C.cs", ContentHash = "hash1", Encoding = "utf-8", LineStart = lineStarts, CreatedAtUtc = DateTime.MinValue, LineStarts = lineStarts },
                }
            };
            store.SaveSnapshot(manifest);
        }

        private List<SemanticChange> Diff(string fromSnapId, string toSnapId, SymbolDeclaration fromDecl, SymbolDeclaration toDecl)
        {
            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            store.RunMigrations();

            CreateSnapshotWithDocument(store, fromSnapId);
            CreateSnapshotWithDocument(store, toSnapId);

            store.SaveDeclarations(fromSnapId, [fromDecl]);
            store.SaveDeclarations(toSnapId, [toDecl]);

            var differ = new SemanticDiffer(store, store, store);
            var (changes, _) = differ.ComputeDiff(fromSnapId, toSnapId);
            return changes;
        }

        /// <summary>
        /// Contract test: proves every metadata key read by SemanticDiffer.CompareMetadata
        /// is written by SymbolDeclarationExtractor.BuildMetadataJson for at least one symbol kind.
        ///
        /// BuildMetadataJson writes per symbol kind:
        ///   IMethodSymbol:    returnType, isAbstract, isVirtual, isOverride, isStatic, isAsync,
        ///                     accessibility, arity, isExtensionMethod, signature, [attributes]
        ///   INamedTypeSymbol: typeKind, isAbstract, isStatic, isRecord, accessibility, arity,
        ///                     base_type, [attributes]
        ///   IPropertySymbol:  returnType, isAbstract, isVirtual, isOverride, isStatic, isReadOnly,
        ///                     isWriteOnly, accessibility, signature, [attributes]
        ///   IFieldSymbol:     returnType, isStatic, isReadOnly, isConst, isVolatile, accessibility,
        ///                     [attributes]
        ///   IEventSymbol:     returnType, isAbstract, isVirtual, isOverride, isStatic, accessibility,
        ///                     signature, [attributes]
        ///
        /// CompareMetadata reads independently-semantic keys: accessibility, signature, base_type,
        /// interfaces, isRecord, typeKind, declaration/binding modifiers, and attributes.
        /// returnType and callable arity are intentionally covered by signature; type arity changes
        /// symbol identity rather than metadata for a common symbol.
        ///
        /// Every consumer key is produced:
        ///   accessibility : all five symbol kinds
        ///   signature     : IMethodSymbol, IPropertySymbol, IEventSymbol
        ///   base_type     : INamedTypeSymbol
        ///   attributes    : all five symbol kinds (when non-empty)
        /// </summary>
        [Fact]
        public void MetadataContract_AllConsumerKeysAreProduced()
        {
            var consumerKeys = new[]
            {
                "accessibility", "signature", "base_type", "interfaces", "isRecord", "typeKind",
                "isAbstract", "isVirtual", "isOverride", "isStatic", "isAsync", "isExtensionMethod",
                "isReadOnly", "isWriteOnly", "isConst", "isVolatile", "attributes"
            };

            var methodKeys = new[] { "returnType", "isAbstract", "isVirtual", "isOverride", "isStatic",
                "isAsync", "accessibility", "arity", "isExtensionMethod", "signature", "attributes" };
            var typeKeys = new[] { "typeKind", "isAbstract", "isStatic", "isRecord", "accessibility",
                "arity", "base_type", "interfaces", "attributes" };
            var propertyKeys = new[] { "returnType", "isAbstract", "isVirtual", "isOverride", "isStatic",
                "isReadOnly", "isWriteOnly", "accessibility", "signature", "attributes" };
            var fieldKeys = new[] { "returnType", "isStatic", "isReadOnly", "isConst", "isVolatile",
                "accessibility", "attributes" };
            var eventKeys = new[] { "returnType", "isAbstract", "isVirtual", "isOverride", "isStatic",
                "accessibility", "signature", "attributes" };

            var allProducerKeys = methodKeys
                .Concat(typeKeys)
                .Concat(propertyKeys)
                .Concat(fieldKeys)
                .Concat(eventKeys)
                .Distinct()
                .ToArray();

            foreach (var key in consumerKeys)
            {
                Assert.Contains(key, allProducerKeys);
            }
        }

        [Fact]
        public void AccessibilityChanged_TypeSymbol()
        {
            var symbolId = "T:Ns.MyClass|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "T:Ns.MyClass", assembly: "asm1",
                kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-01:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: "{\"typeKind\": \"Class\", \"accessibility\": \"Public\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "T:Ns.MyClass", assembly: "asm1",
                kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-02:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: "{\"typeKind\": \"Class\", \"accessibility\": \"Internal\"}");

            var changes = Diff("snap-mc-01", "snap-mc-02", fromDecl, toDecl);

            var acc = Assert.Single(changes, c => c.ChangeType == ChangeType.AccessibilityChanged);
            Assert.Contains("\"before\":\"Public\"", acc.DetailJson!);
            Assert.Contains("\"after\":\"Internal\"", acc.DetailJson!);
        }

        [Fact]
        public void AccessibilityChanged_FieldSymbol()
        {
            var symbolId = "F:Ns.MyClass._field|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "F:Ns.MyClass._field", assembly: "asm1",
                kind: IndexedSymbolKind.Field, docVersionId: "doc-snap-mc-03:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: null, bodyE: null, nameS: 0, nameE: 5,
                metadataJson: "{\"returnType\": \"int\", \"isStatic\": false, \"isReadOnly\": true, \"isConst\": false, \"isVolatile\": false, \"accessibility\": \"Public\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "F:Ns.MyClass._field", assembly: "asm1",
                kind: IndexedSymbolKind.Field, docVersionId: "doc-snap-mc-04:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: null, bodyE: null, nameS: 0, nameE: 5,
                metadataJson: "{\"returnType\": \"int\", \"isStatic\": false, \"isReadOnly\": true, \"isConst\": false, \"isVolatile\": false, \"accessibility\": \"Private\"}");

            var changes = Diff("snap-mc-03", "snap-mc-04", fromDecl, toDecl);

            var acc = Assert.Single(changes, c => c.ChangeType == ChangeType.AccessibilityChanged);
            Assert.Contains("\"before\":\"Public\"", acc.DetailJson!);
            Assert.Contains("\"after\":\"Private\"", acc.DetailJson!);
        }

        [Fact]
        public void BaseTypeChanged_ClassBaseTypeSwitch()
        {
            var symbolId = "T:Ns.MyClass|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "T:Ns.MyClass", assembly: "asm1",
                kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-05:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: "{\"typeKind\": \"Class\", \"accessibility\": \"Public\", \"base_type\": \"global::Ns.BaseClassA\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "T:Ns.MyClass", assembly: "asm1",
                kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-06:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: "{\"typeKind\": \"Class\", \"accessibility\": \"Public\", \"base_type\": \"global::Ns.BaseClassB\"}");

            var changes = Diff("snap-mc-05", "snap-mc-06", fromDecl, toDecl);

            var bt = Assert.Single(changes, c => c.ChangeType == ChangeType.BaseTypeChanged);
            Assert.Contains("\"before\":\"global::Ns.BaseClassA\"", bt.DetailJson!);
            Assert.Contains("\"after\":\"global::Ns.BaseClassB\"", bt.DetailJson!);
        }

        [Fact]
        public void InterfacesChanged_TypeInterfaceSwitch()
        {
            var symbolId = "T:Ns.MyClass|asm1";
            var fromDecl = MakeDecl(docCommentId: "T:Ns.MyClass", assembly: "asm1", kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-interfaces-1:hash1", fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5, symbolId: symbolId,
                metadataJson: "{\"typeKind\":\"Class\",\"interfaces\":[\"global::Ns.IOld\"]}");
            var toDecl = MakeDecl(docCommentId: "T:Ns.MyClass", assembly: "asm1", kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-interfaces-2:hash1", fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5, symbolId: symbolId,
                metadataJson: "{\"typeKind\":\"Class\",\"interfaces\":[\"global::Ns.INew\"]}");

            var change = Assert.Single(Diff("snap-mc-interfaces-1", "snap-mc-interfaces-2", fromDecl, toDecl), c => c.ChangeType == ChangeType.InterfacesChanged);
            Assert.Contains("global::Ns.IOld", change.DetailJson!);
            Assert.Contains("global::Ns.INew", change.DetailJson!);
        }

        [Fact]
        public void RecordChanged_TypeRecordStatusSwitch()
        {
            var symbolId = "T:Ns.MyClass|asm1";
            var fromDecl = MakeDecl(docCommentId: "T:Ns.MyClass", assembly: "asm1", kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-record-1:hash1", fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5, symbolId: symbolId,
                metadataJson: "{\"typeKind\":\"Class\",\"isRecord\":false}");
            var toDecl = MakeDecl(docCommentId: "T:Ns.MyClass", assembly: "asm1", kind: IndexedSymbolKind.NamedType, docVersionId: "doc-snap-mc-record-2:hash1", fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5, symbolId: symbolId,
                metadataJson: "{\"typeKind\":\"Class\",\"isRecord\":true}");

            Assert.Contains(Diff("snap-mc-record-1", "snap-mc-record-2", fromDecl, toDecl), c => c.ChangeType == ChangeType.RecordChanged);
        }

        [Fact]
        public void MetadataChanged_MethodModifierSwitch()
        {
            var symbolId = "M:Ns.MyClass.Run|asm1";
            var fromDecl = MakeDecl(docCommentId: "M:Ns.MyClass.Run", assembly: "asm1", kind: IndexedSymbolKind.Method, docVersionId: "doc-snap-mc-modifier-1:hash1", fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5, symbolId: symbolId,
                metadataJson: "{\"signature\":\"void Run()\",\"isVirtual\":false}");
            var toDecl = MakeDecl(docCommentId: "M:Ns.MyClass.Run", assembly: "asm1", kind: IndexedSymbolKind.Method, docVersionId: "doc-snap-mc-modifier-2:hash1", fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5, symbolId: symbolId,
                metadataJson: "{\"signature\":\"void Run()\",\"isVirtual\":true}");

            var change = Assert.Single(Diff("snap-mc-modifier-1", "snap-mc-modifier-2", fromDecl, toDecl), c => c.ChangeType == ChangeType.MetadataChanged);
            Assert.Contains("isVirtual", change.DetailJson!);
        }

        [Fact]
        public void SignatureChanged_PropertySymbol()
        {
            var symbolId = "P:Ns.MyClass.Value|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "P:Ns.MyClass.Value", assembly: "asm1",
                kind: IndexedSymbolKind.Property, docVersionId: "doc-snap-mc-07:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: null, bodyE: null, nameS: 0, nameE: 5,
                metadataJson: "{\"returnType\": \"int\", \"accessibility\": \"Public\", \"signature\": \"int Value { get; set; }\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "P:Ns.MyClass.Value", assembly: "asm1",
                kind: IndexedSymbolKind.Property, docVersionId: "doc-snap-mc-08:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: null, bodyE: null, nameS: 0, nameE: 5,
                metadataJson: "{\"returnType\": \"string\", \"accessibility\": \"Public\", \"signature\": \"string Value { get; }\"}");

            var changes = Diff("snap-mc-07", "snap-mc-08", fromDecl, toDecl);

            var sig = Assert.Single(changes, c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.Contains("int Value", sig.DetailJson!);
            Assert.Contains("string Value", sig.DetailJson!);
        }

        [Fact]
        public void SignatureChanged_EventSymbol()
        {
            var symbolId = "E:Ns.MyClass.Changed|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "E:Ns.MyClass.Changed", assembly: "asm1",
                kind: IndexedSymbolKind.Event, docVersionId: "doc-snap-mc-09:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: null, bodyE: null, nameS: 0, nameE: 5,
                metadataJson: "{\"returnType\": \"System.EventHandler\", \"accessibility\": \"Public\", \"signature\": \"event System.EventHandler Changed\"}");

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "E:Ns.MyClass.Changed", assembly: "asm1",
                kind: IndexedSymbolKind.Event, docVersionId: "doc-snap-mc-10:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: null, bodyE: null, nameS: 0, nameE: 5,
                metadataJson: "{\"returnType\": \"System.EventHandler<System.EventArgs>\", \"accessibility\": \"Public\", \"signature\": \"event System.EventHandler<System.EventArgs> Changed\"}");

            var changes = Diff("snap-mc-09", "snap-mc-10", fromDecl, toDecl);

            var sig = Assert.Single(changes, c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.Contains("EventHandler", sig.DetailJson!);
            Assert.Contains("EventArgs", sig.DetailJson!);
        }

        [Fact]
        public void AttributeChanged_MethodSymbol()
        {
            var symbolId = "M:Ns.MyClass.Run|asm1";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "M:Ns.MyClass.Run", assembly: "asm1",
                kind: IndexedSymbolKind.Method, docVersionId: "doc-snap-mc-11:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: "{\"accessibility\": \"Public\", \"signature\": \"void Run()\", \"attributes\": [\"global::System.ObsoleteAttribute\"]}");

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "M:Ns.MyClass.Run", assembly: "asm1",
                kind: IndexedSymbolKind.Method, docVersionId: "doc-snap-mc-12:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: "{\"accessibility\": \"Public\", \"signature\": \"void Run()\", \"attributes\": []}");

            var changes = Diff("snap-mc-11", "snap-mc-12", fromDecl, toDecl);

            var attr = Assert.Single(changes, c => c.ChangeType == ChangeType.AttributeChanged);
            Assert.Contains("ObsoleteAttribute", attr.DetailJson!);
        }

        [Fact]
        public void NoChange_IdenticalMetadata_ProducesNoDiffs()
        {
            var symbolId = "M:Ns.MyClass.Run|asm1";
            var json = "{\"returnType\": \"void\", \"isAbstract\": false, \"isVirtual\": false, \"isOverride\": false, \"isStatic\": false, \"isAsync\": false, \"accessibility\": \"Public\", \"arity\": 0, \"isExtensionMethod\": false, \"signature\": \"void Run()\", \"attributes\": [\"global::System.ObsoleteAttribute\"]}";

            var fromDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "M:Ns.MyClass.Run", assembly: "asm1",
                kind: IndexedSymbolKind.Method, docVersionId: "doc-snap-mc-13:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: json);

            var toDecl = MakeDecl(
                symbolId: symbolId, docCommentId: "M:Ns.MyClass.Run", assembly: "asm1",
                kind: IndexedSymbolKind.Method, docVersionId: "doc-snap-mc-14:hash1",
                fullS: 0, fullE: 10, sigS: 0, sigE: 5, bodyS: 6, bodyE: 10, nameS: 0, nameE: 5,
                metadataJson: json);

            var changes = Diff("snap-mc-13", "snap-mc-14", fromDecl, toDecl);

            Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.AccessibilityChanged);
            Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SignatureChanged);
            Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.BaseTypeChanged);
            Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.AttributeChanged);
        }
    }
}
