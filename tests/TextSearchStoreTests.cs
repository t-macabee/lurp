using Lurp.Storage;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Lurp.Tests;

public sealed class TextSearchStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIndexStore _store;
    private const string SnapshotId = "test-snap-1";

    public TextSearchStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lurp_textsearch_{Guid.NewGuid():N}.db");
        _store = new SqliteIndexStore(_dbPath);
        _store.Open();
        _store.RunMigrations();
        Seed();
    }

    public void Dispose()
    {
        _store.Close();
        TestDatabase.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private void Seed()
    {
        using var conn = TestDatabase.Open(_dbPath);
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }

        InsertDoc(conn, "src/TestProject/Foo.cs", "namespace TestProject;\n\npublic class Foo\n{\n    public void Bar()\n    {\n        var x = (string?)null;\n        var y = \"Newpassword1!\";\n        var z = \"Newpassword1!\";\n        // comment with (string?)null\n        var w = \"hello\";\n    }\n}\n", "doc-foo", "ver-foo", isGenerated: false);
        InsertDoc(conn, "src/TestProject/Baz.cs", "namespace TestProject;\n\npublic class Baz\n{\n    public string Password => \"Newpassword1!\";\n    public void Qux()\n    {\n        var a = (string?)null;\n    }\n}\n", "doc-baz", "ver-baz", isGenerated: false);
        InsertDoc(conn, "src/TestProject/Generated.g.cs", "namespace TestProject;\n\npublic class Generated\n{\n    public void M() { }\n}\n", "doc-gen", "ver-gen", isGenerated: true);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO symbols (symbol_id, doc_comment_id, assembly_identity, kind) VALUES ('S:gen', 'T:TestProject.Generated', 'TestProject, Version=1.0.0.0', 'Type');";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO declarations (symbol_id, document_version_id, full_start, full_end, is_generated) VALUES ('S:gen', 'ver-gen', 0, 10, 1);";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
    }

    private static void InsertDoc(SqliteConnection conn, string relPath, string content, string docId, string versionId, bool isGenerated = false, string snapshotId = SnapshotId)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var lineStarts = LineBreaks.ComputeLineStartsJson(bytes);
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "INSERT OR IGNORE INTO documents (document_id, relative_path) VALUES (@did, @path);";
        cmd2.Parameters.AddWithValue("@did", docId);
        cmd2.Parameters.AddWithValue("@path", relPath);
        cmd2.ExecuteNonQuery();
        using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = "INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts, is_generated) VALUES (@vid, @did, @hash, @content, 'utf-8', @bc, @ls, @gen);";
        cmd3.Parameters.AddWithValue("@vid", versionId);
        cmd3.Parameters.AddWithValue("@did", docId);
        cmd3.Parameters.AddWithValue("@hash", hash);
        cmd3.Parameters.AddWithValue("@content", bytes);
        cmd3.Parameters.AddWithValue("@bc", bytes.Length);
        cmd3.Parameters.AddWithValue("@ls", lineStarts);
        cmd3.Parameters.AddWithValue("@gen", isGenerated ? 1 : 0);
        cmd3.ExecuteNonQuery();
        using var cmd4 = conn.CreateCommand();
        cmd4.CommandText = "INSERT OR IGNORE INTO snapshot_documents (snapshot_id, document_version_id) VALUES (@sid, @vid);";
        cmd4.Parameters.AddWithValue("@sid", snapshotId);
        cmd4.Parameters.AddWithValue("@vid", versionId);
        cmd4.ExecuteNonQuery();
    }

    [Fact]
    public void Grep_FindsExactStringWithLineNumbers()
    {
        var page = _store.SearchTextPage("(string?)null", SnapshotId, 50, false, false, null);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
        // Ordered by path then offset: Baz.cs before Foo.cs? Actually Foo.cs path "src/TestProject/Foo.cs" < "src/TestProject/Baz.cs"? Wait alphabetical: Baz < Foo? Let's check: "Baz" vs "Foo": B < F, so Baz.cs first.
        // Our store orders by relative_path ascending, so Baz.cs first, Foo.cs second.
        // Baz has 1, Foo has 2 => total 3, ordering: Baz first, Foo next two
        var first = page.Items[0];
        Assert.Equal("src/TestProject/Baz.cs", first.DocumentPath);
        Assert.Equal(8, first.StartLine);
        // Column 0-based: "        var a = " is 8 spaces + "var a = " (8) = 16
        Assert.Equal(16, first.StartColumn);
        Assert.Equal(8, first.EndLine);
        // Check line_text
        Assert.Contains("(string?)null", first.LineText);
    }

    [Fact]
    public void Grep_CaseSensitive_RespectsIgnoreCaseFlag()
    {
        var sensitive = _store.SearchTextPage("newpassword1!", SnapshotId, 50, false, false, null);
        Assert.Equal(0, sensitive.TotalCount);
        var insensitive = _store.SearchTextPage("newpassword1!", SnapshotId, 50, false, true, null);
        Assert.Equal(3, insensitive.TotalCount);
    }

    [Fact]
    public void Grep_IgnoreCase_MatchesNonAsciiLetters()
    {
        const string snap = "snap-uni";
        using (var conn = TestDatabase.Open(_dbPath))
        {
            using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF;"; pragma.ExecuteNonQuery(); }
            InsertDoc(conn, "P/Uni.cs", "namespace Ns;\n// ÜNÏCODE marker\nclass Uni { }\n", "doc-uni", "ver-uni", snapshotId: snap);
        }

        var page = _store.SearchTextPage("ünïcode", snap, 50, includeGenerated: false, ignoreCase: true, cursor: null);
        Assert.Equal(1, page.TotalCount);
        var item = Assert.Single(page.Items);
        Assert.Equal("P/Uni.cs", item.DocumentPath);
        Assert.Equal(2, item.StartLine);
        Assert.Equal(3, item.StartColumn);
        Assert.Equal(2, item.EndLine);
        Assert.Equal(10, item.EndColumn);
        Assert.Equal("// ÜNÏCODE marker", item.LineText);
    }

    [Fact]
    public void Grep_Pagination_ReturnsEachPageInOrder()
    {
        const string snap = "snap-page";
        using (var conn = TestDatabase.Open(_dbPath))
        {
            using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF;"; pragma.ExecuteNonQuery(); }
            InsertDoc(conn, "a/One.cs", "Foo Foo\nFoo\n", "doc-one", "ver-one", snapshotId: snap);
            InsertDoc(conn, "b/Two.cs", "Foo\n", "doc-two", "ver-two", snapshotId: snap);
        }

        var page1 = _store.SearchTextPage("Foo", snap, 2, includeGenerated: false, ignoreCase: false, cursor: null);
        Assert.Equal(4, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);
        Assert.Equal(("a/One.cs", 1, 0), (page1.Items[0].DocumentPath, page1.Items[0].StartLine, page1.Items[0].StartColumn));
        Assert.Equal(("a/One.cs", 1, 4), (page1.Items[1].DocumentPath, page1.Items[1].StartLine, page1.Items[1].StartColumn));

        var cursor = TextSearchCursor.TryDecode(page1.NextCursor!);
        Assert.NotNull(cursor);
        var page2 = _store.SearchTextPage("Foo", snap, 2, includeGenerated: false, ignoreCase: false, cursor: cursor);
        Assert.Equal(4, page2.TotalCount);
        Assert.Equal(2, page2.Items.Count);
        Assert.Null(page2.NextCursor);
        Assert.Equal(("a/One.cs", 2, 0), (page2.Items[0].DocumentPath, page2.Items[0].StartLine, page2.Items[0].StartColumn));
        Assert.Equal(("b/Two.cs", 1, 0), (page2.Items[1].DocumentPath, page2.Items[1].StartLine, page2.Items[1].StartColumn));
    }

    [Fact]
    public void Grep_IncludeGenerated_FiltersCorrectly()
    {
        var without = _store.SearchTextPage("Generated", SnapshotId, 50, false, false, null);
        Assert.Equal(0, without.TotalCount);
        var with = _store.SearchTextPage("Generated", SnapshotId, 50, true, false, null);
        Assert.True(with.TotalCount > 0);
        Assert.Contains(with.Items, r => r.DocumentPath == "src/TestProject/Generated.g.cs");
    }

    [Fact]
    public void Grep_IncludeGenerated_FiltersDeclarationLessGeneratedDocument()
    {
        using (var conn = TestDatabase.Open(_dbPath))
        {
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=OFF;";
                pragma.ExecuteNonQuery();
            }

            InsertDoc(
                conn,
                "src/TestProject/OnlyUsings.g.cs",
                "// <auto-generated/>\nglobal using System;\n// Generated marker comment\n",
                "doc-only-usings",
                "ver-only-usings",
                isGenerated: true);
        }

        var without = _store.SearchTextPage("Generated", SnapshotId, 50, includeGenerated: false, ignoreCase: false, cursor: null);
        Assert.Equal(0, without.TotalCount);

        var with = _store.SearchTextPage("Generated", SnapshotId, 50, includeGenerated: true, ignoreCase: false, cursor: null);
        Assert.Contains(with.Items, r => r.DocumentPath == "src/TestProject/Generated.g.cs");
        Assert.Contains(with.Items, r => r.DocumentPath == "src/TestProject/OnlyUsings.g.cs");
    }

    [Fact]
    public void Grep_Occurrences_DoNotOverlap()
    {
        const string snap = "snap-overlap";
        using (var conn = TestDatabase.Open(_dbPath))
        {
            using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF;"; pragma.ExecuteNonQuery(); }
            InsertDoc(conn, "P/A.cs", "var s = \"aaa\";\n", "doc-pa", "ver-pa", snapshotId: snap);
            InsertDoc(conn, "P/B.cs", "var t = \"aaaa\";\n", "doc-pb", "ver-pb", snapshotId: snap);
        }

        var page = _store.SearchTextPage("aa", snap, 50, includeGenerated: false, ignoreCase: false, cursor: null);
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(
            [("P/A.cs", 1, 9), ("P/B.cs", 1, 9), ("P/B.cs", 1, 11)],
            page.Items.Select(i => (i.DocumentPath, i.StartLine, i.StartColumn)).ToArray());
    }

    [Fact]
    public void Grep_Cursor_InvalidFingerprint_Throws()
    {
        var page1 = _store.SearchTextPage("Newpassword1!", SnapshotId, 2, false, false, null);
        Assert.NotNull(page1.NextCursor);
        var bad = new TextSearchCursor(SnapshotId, "badfp", page1.Items[0].DocumentPath, page1.Items[0].StartOffset);
        Assert.Throws<ArgumentException>(() => _store.SearchTextPage("Newpassword1!", SnapshotId, 2, false, false, bad));
    }
}
