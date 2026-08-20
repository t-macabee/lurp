using Lurp.Storage;
using Microsoft.Data.Sqlite;
using System.Text;

namespace Lurp.Tests;

public sealed class TextSearchStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteIndexStore _store;
    private const string SnapshotId = "test-snap-1";
    private const string WorkspaceId = "ws-1";

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
        try { _store.Close(); } catch {}
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch {}
        try { if (File.Exists(_dbPath + "-wal")) File.Delete(_dbPath + "-wal"); } catch {}
        try { if (File.Exists(_dbPath + "-shm")) File.Delete(_dbPath + "-shm"); } catch {}
        GC.SuppressFinalize(this);
    }

    private void Seed()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=OFF;";
            pragma.ExecuteNonQuery();
        }

        InsertDoc(conn, "src/TestProject/Foo.cs", "namespace TestProject;\n\npublic class Foo\n{\n    public void Bar()\n    {\n        var x = (string?)null;\n        var y = \"Newpassword1!\";\n        var z = \"Newpassword1!\";\n        // comment with (string?)null\n        var w = \"hello\";\n    }\n}\n", "doc-foo", "ver-foo");
        InsertDoc(conn, "src/TestProject/Baz.cs", "namespace TestProject;\n\npublic class Baz\n{\n    public string Password => \"Newpassword1!\";\n    public void Qux()\n    {\n        var a = (string?)null;\n    }\n}\n", "doc-baz", "ver-baz");
        InsertDoc(conn, "src/TestProject/Generated.g.cs", "namespace TestProject;\n\npublic class Generated\n{\n    public void M() { }\n}\n", "doc-gen", "ver-gen");
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
        // Reopen store to see new data
        _store.Close();
        _store.Open();
    }

    private static void InsertDoc(SqliteConnection conn, string relPath, string content, string docId, string versionId)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var lineStarts = ComputeLineStarts(bytes);
        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "INSERT OR IGNORE INTO documents (document_id, relative_path) VALUES (@did, @path);";
        cmd2.Parameters.AddWithValue("@did", docId);
        cmd2.Parameters.AddWithValue("@path", relPath);
        cmd2.ExecuteNonQuery();
        using var cmd3 = conn.CreateCommand();
        cmd3.CommandText = "INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts) VALUES (@vid, @did, @hash, @content, 'utf-8', @bc, @ls);";
        cmd3.Parameters.AddWithValue("@vid", versionId);
        cmd3.Parameters.AddWithValue("@did", docId);
        cmd3.Parameters.AddWithValue("@hash", hash);
        cmd3.Parameters.AddWithValue("@content", bytes);
        cmd3.Parameters.AddWithValue("@bc", bytes.Length);
        cmd3.Parameters.AddWithValue("@ls", lineStarts);
        cmd3.ExecuteNonQuery();
        using var cmd4 = conn.CreateCommand();
        cmd4.CommandText = "INSERT OR IGNORE INTO snapshot_documents (snapshot_id, document_version_id) VALUES (@sid, @vid);";
        cmd4.Parameters.AddWithValue("@sid", SnapshotId);
        cmd4.Parameters.AddWithValue("@vid", versionId);
        cmd4.ExecuteNonQuery();
    }

    private static string ComputeLineStarts(byte[] bytes)
    {
        var offs = new List<int>{0};
        for (int i=0;i<bytes.Length;i++) if (bytes[i]==(byte)'\n' && i+1 < bytes.Length) offs.Add(i+1);
        return System.Text.Json.JsonSerializer.Serialize(offs);
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
        // Baz's match is at line 9? Let's count: 1 namespace,2 blank,3 public class Baz,4 {,5     public string Password...,6     public void Qux(),7     {,8         var a = (string?)null;,9     },10 }
        // Actually content we inserted: "namespace TestProject;\n\npublic class Baz\n{\n    public string Password => \"Newpassword1!\";\n    public void Qux()\n    {\n        var a = (string?)null;\n    }\n}\n"
        // Lines: 1 namespace,2 empty,3 public class Baz,4 {,5     public string...,6     public void Qux(),7     {,8         var a = (string?)null;,9     },10 }
        // So line 8 is the match
        Assert.Equal(8, first.StartLine);
        // Column 0-based: "        var a = " is 8 spaces + "var a = " (8) =16
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
    public void Grep_Pagination_Works()
    {
        var page1 = _store.SearchTextPage("Newpassword1!", SnapshotId, 2, false, false, null);
        Assert.Equal(3, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.NotNull(page1.NextCursor);
        var cursor = TextSearchCursor.TryDecode(page1.NextCursor!);
        Assert.NotNull(cursor);
        var page2 = _store.SearchTextPage("Newpassword1!", SnapshotId, 2, false, false, cursor);
        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);
        Assert.Equal(3, page2.TotalCount);
        // All items distinct
        var allPaths = page1.Items.Select(i => i.DocumentPath + ":" + i.StartLine).Concat(page2.Items.Select(i => i.DocumentPath + ":" + i.StartLine)).ToList();
        Assert.Equal(3, allPaths.Distinct().Count());
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
    public void Grep_Overlapping_Matches()
    {
        // Insert a doc with "aaa" for overlapping test
        using var conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using (var pragma = conn.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF;"; pragma.ExecuteNonQuery(); }
        var content = "aaa";
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        var ls = "[0]";
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO documents (document_id, relative_path) VALUES ('doc-aaa', 'src/TestProject/Aaa.cs');";
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO document_versions (document_version_id, document_id, content_hash, content, encoding, byte_count, line_starts) VALUES ('ver-aaa', 'doc-aaa', @hash, @content, 'utf-8', @bc, @ls);";
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.Parameters.AddWithValue("@content", bytes);
            cmd.Parameters.AddWithValue("@bc", bytes.Length);
            cmd.Parameters.AddWithValue("@ls", ls);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO snapshot_documents (snapshot_id, document_version_id) VALUES (@sid, 'ver-aaa');";
            cmd.Parameters.AddWithValue("@sid", SnapshotId);
            cmd.ExecuteNonQuery();
        }
        conn.Close();
        _store.Close();
        _store.Open();
        var page = _store.SearchTextPage("aa", SnapshotId, 50, false, false, null);
        // "aaa" contains "aa" twice overlapping at offsets 0 and 1
        Assert.Equal(2, page.TotalCount);
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
