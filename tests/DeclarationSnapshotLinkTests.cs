using System.Text;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class DeclarationSnapshotLinkTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lurp-decl-link-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        TestDatabase.Delete(_dbPath);
    }

    private SqliteIndexStore OpenStore()
    {
        var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    private static SnapshotRow MakeSnapshot(string id, DocumentVersion doc, DateTime createdAtUtc)
    {
        return new SnapshotRow
        {
            SnapshotId = id,
            WorkspaceId = "ws-decl-link",
            GitRoot = "/repo",
            SolutionPath = "/repo/Test.slnx",
            SdkVersion = "10.0.100",
            CompilerVersion = "4.0",
            CreatedAtUtc = createdAtUtc,
            DatabaseSchemaVersion = MigrationRunner.LatestVersion,
            OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
            ExtractorVersion = VersionConstants.ExtractorVersion,
            ToolVersion = VersionConstants.ToolVersion,
            Documents = [doc],
            Projects = [],
            SkippedAdapters = []
        };
    }

    private static SymbolDeclaration MakeDeclaration(string docCommentId, string fqn, string documentVersionId, int fullStart, int fullEnd)
    {
        return new SymbolDeclaration
        {
            SymbolId = new SymbolId(docCommentId, "Asm", fqn),
            Kind = IndexedSymbolKind.Type,
            DocumentVersionId = documentVersionId,
            FullSpan = new DeclarationSpan(fullStart, fullEnd),
            SignatureSpan = new DeclarationSpan(fullStart, fullEnd),
            BodySpan = new DeclarationSpan(fullStart, fullEnd),
            NameSpan = new DeclarationSpan(fullStart, fullStart + 1),
            TargetFramework = "net10.0"
        };
    }

    private static DocumentVersion MakeDocument(string docId, string relativePath, string contentText)
    {
        var bytes = Encoding.UTF8.GetBytes(contentText);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == (byte)'\n')
                lineStarts.Add(i + 1);
        }
        return new DocumentVersion(bytes)
        {
            DocumentId = docId,
            FilePath = relativePath,
            ContentHash = "h1",
            Encoding = "utf-8",
            LineStarts = JsonSerializer.Serialize(lineStarts)
        };
    }

    [Fact]
    public void DeleteSnapshotData_RemovesTheDeclarationsThatNoSnapshotLinks()
    {
        using var store = OpenStore();
        var doc = MakeDocument("doc1", "src/N.cs", "namespace N;\npublic class A {}\npublic class B {}\n");
        const string dv1 = "doc1:h1";

        store.SaveSnapshot(MakeSnapshot("s1", doc, DateTime.UtcNow.AddMinutes(-2)));
        store.MarkSnapshotComplete("s1");
        store.SaveSnapshot(MakeSnapshot("s2", doc, DateTime.UtcNow.AddMinutes(-1)));
        store.MarkSnapshotComplete("s2");

        var a = MakeDeclaration("T:N.A", "global::N.A", dv1, 13, 30);
        var b = MakeDeclaration("T:N.B", "global::N.B", dv1, 31, 48);
        store.SaveDeclarations("s1", [a, b]);
        store.SaveDeclarations("s2", [a]);

        store.DeleteSnapshotData("s1");

        using var conn = TestDatabase.OpenReadOnly(_dbPath);
        using var declCmd = conn.CreateCommand();
        declCmd.CommandText = "SELECT symbol_id FROM declarations ORDER BY symbol_id;";
        var declSymbols = new List<string>();
        using (var r = declCmd.ExecuteReader())
        {
            while (r.Read())
                declSymbols.Add(r.GetString(0));
        }
        Assert.Equal([a.SymbolId.Value], declSymbols);

        using var symCmd = conn.CreateCommand();
        symCmd.CommandText = "SELECT symbol_id FROM symbols ORDER BY symbol_id;";
        var symbols = new List<string>();
        using (var r = symCmd.ExecuteReader())
        {
            while (r.Read())
                symbols.Add(r.GetString(0));
        }
        Assert.Equal([a.SymbolId.Value], symbols);
    }

    [Fact]
    public void Readers_ShowOnlyTheLinkedDeclarations()
    {
        using var store = OpenStore();
        // Line 1: "namespace N;\n" (13 bytes, offset 0..12)
        // Line 2 starts at byte offset 13, which lies inside both [12, 30) and [10, 40).
        var doc = MakeDocument("doc1", "src/N.cs", "namespace N;\npublic class B { public void A() {} }\n");
        const string dv1 = "doc1:h1";

        store.SaveSnapshot(MakeSnapshot("s1", doc, DateTime.UtcNow.AddMinutes(-2)));
        store.MarkSnapshotComplete("s1");
        store.SaveSnapshot(MakeSnapshot("s2", doc, DateTime.UtcNow.AddMinutes(-1)));
        store.MarkSnapshotComplete("s2");

        // Make 'a' the smaller span (12..30, length 18) vs 'b' (10..40, length 30) so that without
        // snapshot_declarations filtering, NavigateToLocation picks 'a' by span length tie-break.
        var a = MakeDeclaration("T:N.A", "global::N.A", dv1, 12, 30);
        var b = MakeDeclaration("T:N.B", "global::N.B", dv1, 10, 40);
        store.SaveDeclarations("s1", [a]);
        store.SaveDeclarations("s2", [b]);

        var nav = store.NavigateToLocation("src/N.cs", 2, "s2");
        Assert.NotNull(nav);
        Assert.Equal(b.SymbolId.Value, nav.SymbolId);

        var ids = store.GetSymbolIdsByDocumentVersionIds("s2", [dv1]);
        Assert.Equal([b.SymbolId.Value], ids);

        var locs = store.GetDeclarationLocations(b.SymbolId.Value, "s2");
        Assert.Single(locs);

        var info = Assert.Single(store.FindSymbols(SymbolKey.Fqn, "global::N.B", "s2"));
        Assert.Equal(1, info.DeclarationCount);
    }
}
