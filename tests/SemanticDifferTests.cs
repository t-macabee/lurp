using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace Lurp.Tests;

/// <summary>
///     Direct SemanticDiffer tests: ComputeDiff change-kind emission, ComputeSymbolDiff
///     signature round-trips, MatchTransitions rename pairing, and DiffEdges payload
///     classification. All pure store + declaration construction — no MSBuild, no Roslyn.
///     Restores live coverage deleted in f1254fc (tests/MigrationRunnerTests.B3SemanticChanges.cs).
/// </summary>
public sealed class SemanticDifferTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-semantic-differ-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    private SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    private static byte[] StringToBytes(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    private static void CreateSnapshotWithDocument(SqliteIndexStore store, string snapshotId,
        string? customSource = null, string filePath = "src/Foo.cs")
    {
        var sourceBytes = customSource != null
            ? StringToBytes(customSource)
            : StringToBytes(
                "using System;\n" +
                "namespace TestNs {\n" +
                "    public class Foo {\n" +
                "        public void Bar() { Console.WriteLine(); }\n" +
                "    }\n" +
                "}\n");

        var lineStarts = customSource != null ? "[0]" : "[0,14,33,56,107,113]";

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
                new(sourceBytes)
                {
                    DocumentId = "doc-" + snapshotId, FilePath = filePath, ContentHash = "hash1", Encoding = "utf-8",
                    LineStarts = lineStarts
                }
            }
        };
        store.SaveSnapshot(manifest);
    }

    private static SymbolDeclaration MakeDecl(
        string docCommentId,
        string assembly,
        IndexedSymbolKind kind,
        string docVersionId,
        int? fullS, int? fullE,
        int? sigS, int? sigE,
        int? bodyS, int? bodyE,
        int? nameS, int? nameE,
        bool isPartial = false,
        string? fqn = null,
        string? metadataJson = null,
        string? symbolId = null)
    {
        return new SymbolDeclaration
        {
            SymbolId = symbolId != null
                ? new SymbolId(SymbolId.Parse(symbolId).DocCommentId, SymbolId.Parse(symbolId).AssemblyIdentity, fqn)
                : new SymbolId(docCommentId, assembly, fqn),
            Kind = kind,
            DocumentVersionId = docVersionId,
            FullSpan = new DeclarationSpan(fullS, fullE),
            SignatureSpan = new DeclarationSpan(sigS, sigE),
            BodySpan = new DeclarationSpan(bodyS, bodyE),
            NameSpan = new DeclarationSpan(nameS, nameE),
            IsPartial = isPartial,
            MetadataJson = metadataJson
        };
    }

    // ── ComputeDiff change kinds ─────────────────────────────────────────────

    [Fact]
    public void InterfacesChanged_WhenInterfaceSetChanges()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-interfaces-from";
        const string toSnapshotId = "snap-interfaces-to";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "T:Ns.Foo|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-interfaces-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"interfaces": ["T:Ns.IFoo|asm1"]}""")
        ]);

        store.SaveDeclarations(toSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-interfaces-to:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"interfaces": ["T:Ns.IFoo|asm1", "T:Ns.IBar|asm1"]}""")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        // A single change: the interface set is the only differing field.
        var change = Assert.Single(changes);
        Assert.Equal(ChangeType.InterfacesChanged, change.ChangeType);
        Assert.Equal(symbolId, change.SymbolId);
    }

    [Fact]
    public void RecordChanged_WhenIsRecordFlips()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-record-from";
        const string toSnapshotId = "snap-record-to";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "T:Ns.Foo|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-record-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": false}""")
        ]);

        store.SaveDeclarations(toSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-record-to:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": true}""")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        // A single change: isRecord is the only differing field.
        var change = Assert.Single(changes);
        Assert.Equal(ChangeType.RecordChanged, change.ChangeType);
        Assert.Equal(symbolId, change.SymbolId);
    }

    [Fact]
    public void MetadataChanged_WhenTypeKindChanges()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-typekind-from";
        const string toSnapshotId = "snap-typekind-to";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "T:Ns.Foo|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-typekind-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"typeKind": "Class"}""")
        ]);

        store.SaveDeclarations(toSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-typekind-to:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"typeKind": "Struct"}""")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        // A single change: typeKind is the only differing field.
        var change = Assert.Single(changes);
        Assert.Equal(ChangeType.MetadataChanged, change.ChangeType);
        Assert.Equal(symbolId, change.SymbolId);
    }

    // ── ComputeSymbolDiff signature round-trips (S1/S2/S5/S6) ───────────────

    [Fact]
    public void S1_NullableAnnotationChanged()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-s1-001";
        const string toSnapshotId = "snap-s1-002";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "M:Ns.Foo.Bar|asm1";

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
            metadataJson: """{"signature": "string Bar(string input)", "return_type": "string"}""");

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
            metadataJson: """{"signature": "string Bar(string? input)", "return_type": "string"}""");

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
        Assert.NotNull(signatureChanged);
        Assert.Equal(symbolId, signatureChanged.SymbolId);
        using var s1Detail = JsonDocument.Parse(signatureChanged.DetailJson!);
        Assert.Equal("string Bar(string input)", s1Detail.RootElement.GetProperty("before").GetString());
        Assert.Equal("string Bar(string? input)", s1Detail.RootElement.GetProperty("after").GetString());
    }

    [Fact]
    public void S2_RefParameterModifierChanged()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-s2-001";
        const string toSnapshotId = "snap-s2-002";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "M:Ns.Foo.Baz|asm1";

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
            metadataJson: """{"signature": "void Baz(int value)"}""");

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
            metadataJson: """{"signature": "void Baz(ref int value)"}""");

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
        Assert.NotNull(signatureChanged);
        Assert.Equal(symbolId, signatureChanged.SymbolId);
        using var s2Detail = JsonDocument.Parse(signatureChanged.DetailJson!);
        Assert.Equal("void Baz(int value)", s2Detail.RootElement.GetProperty("before").GetString());
        Assert.Equal("void Baz(ref int value)", s2Detail.RootElement.GetProperty("after").GetString());
    }

    [Fact]
    public void S5_OperatorOverloadSignatureChanged()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-s5-001";
        const string toSnapshotId = "snap-s5-002";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "M:Ns.Money.op_Addition|asm1";

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
            metadataJson: """{"signature": "Money operator +(Money a, Money b)"}""");

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
            metadataJson: """{"signature": "decimal operator +(Money a, Money b)"}""");

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
        Assert.NotNull(signatureChanged);
        Assert.Equal(symbolId, signatureChanged.SymbolId);
        using var s5Detail = JsonDocument.Parse(signatureChanged.DetailJson!);
        Assert.Equal("Money operator +(Money a, Money b)", s5Detail.RootElement.GetProperty("before").GetString());
        Assert.Equal("decimal operator +(Money a, Money b)", s5Detail.RootElement.GetProperty("after").GetString());
    }

    [Fact]
    public void S6_ConversionOperatorSignatureChanged()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-s6-001";
        const string toSnapshotId = "snap-s6-002";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "M:Ns.Fraction.op_Explicit~System.Int32|asm1";

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
            metadataJson: """{"signature": "implicit operator int(Fraction f)"}""");

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
            metadataJson: """{"signature": "explicit operator int(Fraction f)"}""");

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var signatureChanged = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SignatureChanged);
        Assert.NotNull(signatureChanged);
        Assert.Equal(symbolId, signatureChanged.SymbolId);
        using var s6Detail = JsonDocument.Parse(signatureChanged.DetailJson!);
        Assert.Equal("implicit operator int(Fraction f)", s6Detail.RootElement.GetProperty("before").GetString());
        Assert.Equal("explicit operator int(Fraction f)", s6Detail.RootElement.GetProperty("after").GetString());
    }

    // ── MatchTransitions ────────────────────────────────────────────────────

    [Fact]
    public void MatchTransitions_SymbolRemovedAndAddedPair_ReportsSymbolRenamed()
    {
        // Source design: both sources have the same return type, parameter list,
        // and body — only the method name differs ("OldName" vs "NewName").
        // BuildContinuityKey normalizes the name out of the signature hash by
        // replacing [nameStart, nameEnd) with a placeholder before hashing, so
        // the two declarations produce identical continuity keys. SymbolTransitionMatcher
        // pairs them; ClassifyTransition sees different simple names and returns Rename.
        //
        // Span layout for "public void OldName() { }" (25 bytes, ASCII):
        //   [0,  21) = signature  "public void OldName()"
        //   [12, 19) = name       "OldName" / "NewName"  (7 bytes each)
        //   [21, 25) = body       " { }"
        //   [0,  25) = full span
        const string oldNameSource = "public void OldName() { }";
        const string newNameSource = "public void NewName() { }";

        using var store = OpenStore();

        const string fromSnapshotId = "snap-rename-from";
        const string toSnapshotId = "snap-rename-to";
        CreateSnapshotWithDocument(store, fromSnapshotId, oldNameSource);
        CreateSnapshotWithDocument(store, toSnapshotId, newNameSource);

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: "M:Ns.OldName|asm1",
                docCommentId: "M:Ns.OldName",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-rename-from:hash1",
                fullS: 0, fullE: 25,
                sigS: 0, sigE: 21,
                bodyS: 21, bodyE: 25,
                nameS: 12, nameE: 19,
                fqn: "Ns.OldName")
        ]);

        store.SaveDeclarations(toSnapshotId, [
            MakeDecl(
                symbolId: "M:Ns.NewName|asm1",
                docCommentId: "M:Ns.NewName",
                assembly: "asm1",
                kind: IndexedSymbolKind.Method,
                docVersionId: "doc-snap-rename-to:hash1",
                fullS: 0, fullE: 25,
                sigS: 0, sigE: 21,
                bodyS: 21, bodyE: 25,
                nameS: 12, nameE: 19,
                fqn: "Ns.NewName")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        // The raw removed/added pair is consumed by MatchTransitions and
        // re-emitted as a single SymbolRenamed change.
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRemoved);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolAdded);

        var renamed = Assert.Single(changes);
        Assert.Equal(ChangeType.SymbolRenamed, renamed.ChangeType);
        Assert.Equal("M:Ns.NewName|asm1", renamed.SymbolId);
        using var renameDetail = JsonDocument.Parse(renamed.DetailJson!);
        Assert.Equal("M:Ns.OldName|asm1", renameDetail.RootElement.GetProperty("previous_symbol_id").GetString());
        Assert.Equal("M:Ns.NewName|asm1", renameDetail.RootElement.GetProperty("current_symbol_id").GetString());
        Assert.Equal("Rename", renameDetail.RootElement.GetProperty("transition_kind").GetString());
    }

    // ── DiffEdges payload classification ────────────────────────────────────

    [Fact]
    public void SemanticDiffer_EdgeAddedAndRemoved()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-b3-009";
        const string toSnapshotId = "snap-b3-010";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        var fromEdges = new List<EdgeRecord>
        {
            new()
            {
                SourceSymbolId = "M:Ns.Foo|asm1",
                TargetSymbolId = "M:Ns.Bar|asm1",
                Kind = "Calls",
                Provenance = "compiler_proved"
            }
        };
        var toEdges = new List<EdgeRecord>
        {
            new()
            {
                SourceSymbolId = "M:Ns.Bar|asm1",
                TargetSymbolId = "M:Ns.Baz|asm1",
                Kind = "Calls",
                Provenance = "compiler_proved"
            }
        };

        store.SaveEdges(fromSnapshotId, fromEdges);
        store.SaveEdges(toSnapshotId, toEdges);

        var differ = new SemanticDiffer(store, store, store, store);
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

    // ── SymbolRelocated change type ───────────────────────────────────────────

    [Fact]
    public void SymbolRelocated_WhenDeclarationMovesToNewFile()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-relocate-from";
        const string toSnapshotId = "snap-relocate-to";
        CreateSnapshotWithDocument(store, fromSnapshotId, filePath: "src/Foo.cs");
        CreateSnapshotWithDocument(store, toSnapshotId, filePath: "src/Foo2.cs");

        const string symbolId = "T:Ns.Foo|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-relocate-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": false}""")
        ]);

        store.SaveDeclarations(toSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-relocate-to:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": false}""")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var relocated = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRelocated);
        Assert.NotNull(relocated);
        Assert.Equal(symbolId, relocated.SymbolId);

        using var detail = JsonDocument.Parse(relocated.DetailJson!);
        var before = detail.RootElement.GetProperty("before").EnumerateArray().Select(p => p.GetString()).ToList();
        var after = detail.RootElement.GetProperty("after").EnumerateArray().Select(p => p.GetString()).ToList();

        Assert.Single(before);
        Assert.Single(after);
        Assert.Equal("src/Foo.cs", before[0]);
        Assert.Equal("src/Foo2.cs", after[0]);

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolMoved);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRenamed);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolAdded);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRemoved);
    }

    [Fact]
    public void SymbolRelocated_NotEmitted_WhenSymbolAbsentFromOneSnapshot()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-absent-from";
        const string toSnapshotId = "snap-absent-to";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "T:Ns.Foo|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-absent-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": false}""")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRelocated);
        var removed = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRemoved);
        Assert.NotNull(removed);
        Assert.Equal(symbolId, removed.SymbolId);
    }

    [Fact]
    public void SymbolRelocated_PartialType_GainingSecondFile()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-partial-from";
        const string toSnapshotId = "snap-partial-to";
        CreateSnapshotWithDocument(store, fromSnapshotId, filePath: "src/Partial.cs");

        // The to snapshot needs BOTH files: src/Partial.cs (unchanged) and src/Partial2.cs (new).
        // Build the to snapshot manually with two documents so GetDeclarationLocations sees both.
        var partialSource = StringToBytes(
            "using System;\n" +
            "namespace TestNs {\n" +
            "    public class Foo {\n" +
            "        public void Bar() { Console.WriteLine(); }\n" +
            "    }\n" +
            "}\n");
        var partialLineStarts = "[0,14,33,56,107,113]";
        var toManifest = new SnapshotRow
        {
            SnapshotId = toSnapshotId,
            WorkspaceId = "workspace:///root/proj",
            GitRoot = "/root",
            SolutionPath = "/root/proj",
            SdkVersion = "10.0.301",
            CompilerVersion = "4.12.0.0",
            CreatedAtUtc = DateTime.UtcNow,
            Documents = new List<DocumentVersion>
            {
                new(partialSource)
                {
                    DocumentId = "doc-to-partial", FilePath = "src/Partial.cs", ContentHash = "hash1",
                    Encoding = "utf-8", LineStarts = partialLineStarts
                },
                new(partialSource)
                {
                    DocumentId = "doc-to-partial2", FilePath = "src/Partial2.cs", ContentHash = "hash1",
                    Encoding = "utf-8", LineStarts = partialLineStarts
                }
            }
        };
        store.SaveSnapshot(toManifest);

        const string symbolId = "T:Ns.Partial|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Partial",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-partial-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isPartial": true}""",
                isPartial: true)
        ]);

        store.SaveDeclarations(toSnapshotId, new[]
        {
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Partial",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-to-partial:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isPartial": true}""",
                isPartial: true),
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Partial",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-to-partial2:hash1",
                fullS: 20, fullE: 30,
                sigS: 20, sigE: 25,
                bodyS: 26, bodyE: 30,
                nameS: 20, nameE: 25,
                metadataJson: """{"isPartial": true}""",
                isPartial: true)
        });

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var relocated = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRelocated);
        Assert.NotNull(relocated);
        Assert.Equal(symbolId, relocated.SymbolId);

        using var detail = JsonDocument.Parse(relocated.DetailJson!);
        var before = detail.RootElement.GetProperty("before").EnumerateArray().Select(p => p.GetString()).ToList();
        var after = detail.RootElement.GetProperty("after").EnumerateArray().Select(p => p.GetString()).ToList();

        Assert.Single(before);
        Assert.Equal(2, after.Count);
        Assert.Equal("src/Partial.cs", before[0]);
        Assert.Contains("src/Partial.cs", after);
        Assert.Contains("src/Partial2.cs", after);
    }

    [Fact]
    public void SymbolRelocated_NamespaceChange_NoFileChange()
    {
        using var store = OpenStore();

        const string fromSnapshotId = "snap-ns-from";
        const string toSnapshotId = "snap-ns-to";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);

        const string symbolId = "T:Ns.Foo|asm1";

        store.SaveDeclarations(fromSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-ns-from:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": false}""",
                fqn: "Ns.Foo")
        ]);

        store.SaveDeclarations(toSnapshotId, [
            MakeDecl(
                symbolId: symbolId,
                docCommentId: "T:Ns.Foo",
                assembly: "asm1",
                kind: IndexedSymbolKind.NamedType,
                docVersionId: "doc-snap-ns-to:hash1",
                fullS: 0, fullE: 10,
                sigS: 0, sigE: 5,
                bodyS: 6, bodyE: 10,
                nameS: 0, nameE: 5,
                metadataJson: """{"isRecord": false}""",
                fqn: "NewNs.Foo")
        ]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var relocated = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRelocated);
        Assert.Null(relocated);

        var moved = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolMoved);
        Assert.NotNull(moved);
        Assert.Equal(symbolId, moved.SymbolId);
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
        using var store = OpenStore();

        const string fromSnapshotId = "snap-edge-payload-from";
        const string toSnapshotId = "snap-edge-payload-to";
        CreateSnapshotWithDocument(store, fromSnapshotId);
        CreateSnapshotWithDocument(store, toSnapshotId);
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
            SourceEndColumn = 9
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
            SourceEndColumn = fromEdge.SourceEndColumn
        };

        store.SaveEdges(fromSnapshotId, [fromEdge]);
        store.SaveEdges(toSnapshotId, [toEdge]);

        var differ = new SemanticDiffer(store, store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var change = Assert.Single(changes, c => c.ChangeType == expectedChangeType);
        Assert.Equal(fromEdge.SourceSymbolId, change.SymbolId);
        Assert.DoesNotContain(changes, c => c.ChangeType is ChangeType.EdgeAdded or ChangeType.EdgeRemoved);
        Assert.Contains("\"before\"", change.DetailJson!);
        Assert.Contains("\"after\"", change.DetailJson!);
    }
}