using System.Text;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public class SemanticRenameIntegrationTests : IDisposable
{
    private readonly string _dbPath;
    private SqliteIndexStore? _store;

    public SemanticRenameIntegrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"indexer_rename_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        _store?.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static byte[] StringToBytes(string text) => Encoding.UTF8.GetBytes(text);

    private static void CreateSnapshotWithContent(SqliteIndexStore store, string snapshotId, byte[] content)
    {
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
            Documents =
            [
                new DocumentVersion(content)
                {
                    DocumentId = "doc-" + snapshotId,
                    FilePath = "src/Test.cs",
                    ContentHash = "hash1",
                    Encoding = "utf-8",
                    LineStart = lineStarts,
                    CreatedAtUtc = DateTime.MinValue,
                    LineStarts = lineStarts
                }
            ]
        };
        store.SaveSnapshot(manifest);
    }

    private static SymbolDeclaration MakeTransitionDecl(
        string symbolIdValue,
        string? fqn,
        IndexedSymbolKind kind,
        string snapshotId,
        int sigStart, int sigEnd,
        int nameStart, int nameEnd,
        int? bodyStart, int? bodyEnd)
    {
        var parsed = SymbolId.Parse(symbolIdValue);
        return new SymbolDeclaration
        {
            SymbolId = new SymbolId(parsed.DocCommentId, parsed.AssemblyIdentity, fqn),
            Kind = kind,
            DocumentVersionId = "doc-" + snapshotId + ":hash1",
            FullSpan = new DeclarationSpan(sigStart, bodyEnd ?? sigEnd),
            SignatureSpan = new DeclarationSpan(sigStart, sigEnd),
            BodySpan = new DeclarationSpan(bodyStart, bodyEnd),
            NameSpan = new DeclarationSpan(nameStart, nameEnd),
            IsPartial = false,
        };
    }

    [Fact]
    public void ComputeDiff_RealRoslynRename_EmitsRenameWithCurrentSymbolId()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open();
        store.RunMigrations();

        var fromSnapshotId = "snap-ren-from";
        var toSnapshotId = "snap-ren-to";

        var content = StringToBytes("void Foo() { body }");
        CreateSnapshotWithContent(store, fromSnapshotId, content);
        CreateSnapshotWithContent(store, toSnapshotId, content);

        var fromSymbolId = "M:Ns.OldName|asm1";
        var toSymbolId = "M:Ns.NewName|asm1";

        var fromDecl = MakeTransitionDecl(
            fromSymbolId, "Ns.OldName", IndexedSymbolKind.Method,
            fromSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);
        var toDecl = MakeTransitionDecl(
            toSymbolId, "Ns.NewName", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var rename = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRenamed);
        Assert.NotNull(rename);
        Assert.Equal(toSymbolId, rename.SymbolId);
        Assert.Contains(fromSymbolId, rename.DetailJson!);
        Assert.Contains(toSymbolId, rename.DetailJson!);
        Assert.Contains("Rename", rename.DetailJson!);

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolAdded);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRemoved);
    }

    [Fact]
    public void ComputeDiff_SameNameMovedContainer_EmitsMove()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open();
        store.RunMigrations();

        var fromSnapshotId = "snap-mov-from";
        var toSnapshotId = "snap-mov-to";

        var content = StringToBytes("void Foo() { body }");
        CreateSnapshotWithContent(store, fromSnapshotId, content);
        CreateSnapshotWithContent(store, toSnapshotId, content);

        var fromSymbolId = "M:OldNs.Foo|asm1";
        var toSymbolId = "M:NewNs.Foo|asm1";

        var fromDecl = MakeTransitionDecl(
            fromSymbolId, "OldNs.Foo", IndexedSymbolKind.Method,
            fromSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);
        var toDecl = MakeTransitionDecl(
            toSymbolId, "NewNs.Foo", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        var move = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolMoved);
        Assert.NotNull(move);
        Assert.Equal(toSymbolId, move.SymbolId);
        Assert.Contains("Move", move.DetailJson!);

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRenamed);
    }

    [Fact]
    public void ComputeDiff_MethodSignatureChanges_KeepsAddedAndRemoved()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open();
        store.RunMigrations();

        var fromSnapshotId = "snap-sig-from";
        var toSnapshotId = "snap-sig-to";

        var fromContent = StringToBytes("void Foo(int x) { body }");
        var toContent = StringToBytes("void Foo() { body }");
        CreateSnapshotWithContent(store, fromSnapshotId, fromContent);
        CreateSnapshotWithContent(store, toSnapshotId, toContent);

        var fromSymbolId = "M:Ns.Foo(System.Int32)|asm1";
        var toSymbolId = "M:Ns.Foo|asm1";

        var fromDecl = MakeTransitionDecl(
            fromSymbolId, "Ns.Foo", IndexedSymbolKind.Method,
            fromSnapshotId,
            sigStart: 0, sigEnd: 17, nameStart: 5, nameEnd: 8,
            bodyStart: 18, bodyEnd: 24);
        var toDecl = MakeTransitionDecl(
            toSymbolId, "Ns.Foo", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 10, nameStart: 5, nameEnd: 8,
            bodyStart: 11, bodyEnd: 17);

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRenamed);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolMoved);

        var removed = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolRemoved);
        var added = changes.FirstOrDefault(c => c.ChangeType == ChangeType.SymbolAdded);
        Assert.NotNull(removed);
        Assert.NotNull(added);
    }

    [Fact]
    public void ComputeDiff_AmbiguousFingerprint_KeepsAllAddsAndRemoves()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open();
        store.RunMigrations();

        var fromSnapshotId = "snap-amb-from";
        var toSnapshotId = "snap-amb-to";

        var content = StringToBytes("void Same() { body }");
        CreateSnapshotWithContent(store, fromSnapshotId, content);
        CreateSnapshotWithContent(store, toSnapshotId, content);

        var fromDecl1 = MakeTransitionDecl(
            "M:A.Same|asm1", "A.Same", IndexedSymbolKind.Method,
            fromSnapshotId,
            sigStart: 0, sigEnd: 13, nameStart: 5, nameEnd: 9,
            bodyStart: 14, bodyEnd: 20);
        var fromDecl2 = MakeTransitionDecl(
            "M:B.Same|asm1", "B.Same", IndexedSymbolKind.Method,
            fromSnapshotId,
            sigStart: 0, sigEnd: 13, nameStart: 5, nameEnd: 9,
            bodyStart: 14, bodyEnd: 20);

        var toDecl1 = MakeTransitionDecl(
            "M:C.Same|asm1", "C.Same", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 13, nameStart: 5, nameEnd: 9,
            bodyStart: 14, bodyEnd: 20);
        var toDecl2 = MakeTransitionDecl(
            "M:D.Same|asm1", "D.Same", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 13, nameStart: 5, nameEnd: 9,
            bodyStart: 14, bodyEnd: 20);

        store.SaveDeclarations(fromSnapshotId, [fromDecl1, fromDecl2]);
        store.SaveDeclarations(toSnapshotId, [toDecl1, toDecl2]);

        var differ = new SemanticDiffer(store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolRenamed);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.SymbolMoved);

        var removed = changes.Where(c => c.ChangeType == ChangeType.SymbolRemoved).ToList();
        var added = changes.Where(c => c.ChangeType == ChangeType.SymbolAdded).ToList();
        Assert.Equal(2, removed.Count);
        Assert.Equal(2, added.Count);
    }

    [Fact]
    public void TraceImpact_CurrentRenamedSymbol_IncludesTransitionCause()
    {
        var store = new SqliteIndexStore(_dbPath);
        _store = store;
        store.Open();
        store.RunMigrations();

        var fromSnapshotId = "snap-imp-from";
        var toSnapshotId = "snap-imp-to";

        var content = StringToBytes("void Foo() { body }");
        CreateSnapshotWithContent(store, fromSnapshotId, content);
        CreateSnapshotWithContent(store, toSnapshotId, content);

        var fromSymbolId = "M:Ns.OldName|asm1";
        var toSymbolId = "M:Ns.NewName|asm1";

        var fromDecl = MakeTransitionDecl(
            fromSymbolId, "Ns.OldName", IndexedSymbolKind.Method,
            fromSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);
        var toDecl = MakeTransitionDecl(
            toSymbolId, "Ns.NewName", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);

        store.SaveDeclarations(fromSnapshotId, [fromDecl]);
        store.SaveDeclarations(toSnapshotId, [toDecl]);

        var differ = new SemanticDiffer(store, store, store);
        var (changes, _) = differ.ComputeDiff(fromSnapshotId, toSnapshotId);

        store.SaveSemanticChanges(fromSnapshotId, toSnapshotId, changes);

        var targetSymbolId = "M:Ns.Target|asm1";
        var targetDecl = MakeTransitionDecl(
            targetSymbolId, "Ns.Target", IndexedSymbolKind.Method,
            toSnapshotId,
            sigStart: 0, sigEnd: 12, nameStart: 5, nameEnd: 8,
            bodyStart: 13, bodyEnd: 19);
        store.SaveDeclarations(toSnapshotId, [targetDecl]);

        store.SaveEdges(toSnapshotId,
        [
            new EdgeRecord
            {
                SourceSymbolId = toSymbolId,
                TargetSymbolId = targetSymbolId,
                Kind = "Calls",
                Provenance = "compiler_proved",
                SnapshotId = toSnapshotId,
            }
        ]);

        var traverser = new ImpactTraverser(store, toSnapshotId, store);
        var paths = traverser.TraceImpact(toSymbolId, ImpactDirection.Downstream);

        Assert.NotEmpty(paths);
        var causes = paths.SelectMany(p => p.SemanticCauses).ToList();
        Assert.Contains(causes, c =>
            c.SymbolId == toSymbolId && c.ChangeType == ChangeType.SymbolRenamed);
    }
}
