using Lurp.Parity.Shared;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

[Trait("Category", "Slow")]
public sealed class SnapshotComparisonTests : IntegrationTestBase
{
    [Fact]
    public async Task Compare_ReportsADifferentLineTable()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        using (var connection = TestDatabase.Open(copy))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TRIGGER trg_document_versions_immutable;
                UPDATE document_versions SET line_starts = '[0,15,16,32,34]'
                WHERE document_id IN (SELECT document_id FROM documents WHERE relative_path = 'src/App/Calc.cs');
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        // Line 5 starts at 39 in Calc.cs, but the changed table puts it at 34.
        var difference = Assert.Single(SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
        Assert.StartsWith("snapshot_documents:", difference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_ReportsADifferentBareFqn()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        using (var connection = TestDatabase.Open(copy))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE snapshot_symbols SET bare_fqn = 'Wrong.Name' WHERE fqn = 'global::App.Calc<T>';
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var difference = Assert.Single(SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
        Assert.StartsWith("snapshot_symbols:", difference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_ReportsAColumnThatIsNeitherComparedNorExcluded()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        foreach (var path in new[] { DbPath, copy })
        {
            using var connection = TestDatabase.Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE edges ADD COLUMN probe INTEGER;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(["column edges.probe is neither compared nor excluded"],
            SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
    }

    [Fact]
    public async Task Compare_ReportsATableThatIsNeitherComparedNorExcluded()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();

        foreach (var path in new[] { DbPath, copy })
        {
            using var connection = TestDatabase.Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE extra_facts (x INTEGER)";
            command.ExecuteNonQuery();
        }

        Assert.Equal(["table extra_facts is neither compared nor excluded"],
            SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
    }

    [Fact]
    public async Task Compare_ReportsAStoredSymbolIdentityDifference()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        using (var connection = TestDatabase.Open(copy))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE symbols SET doc_comment_id = 'T:Wrong';
                """;
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        var difference = Assert.Single(SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
        Assert.StartsWith("symbols:", difference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compare_RedirectedDiagnostics_IgnoreCacheRootOnly()
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        AddDiagnostic(DbPath, snapshot, "/tmp/cache-a/lurp/0123456789ab/obj/App/net10.0/App.AssemblyInfo.cs");
        AddDiagnostic(copy, snapshot, "/tmp/cache-b/lurp/0123456789ab/obj/App/net10.0/App.AssemblyInfo.cs");

        Assert.Empty(SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot));
    }

    [Theory]
    [InlineData("/tmp/cache-b/lurp/0123456789ab/obj/App/net9.0/App.AssemblyInfo.cs")]
    [InlineData("/tmp/cache-b/lurp/0123456789ab/obj/Other/net10.0/App.AssemblyInfo.cs")]
    [InlineData("/tmp/cache-b/lurp/0123456789ac/obj/App/net10.0/App.AssemblyInfo.cs")]
    [InlineData("/tmp/cache-b/not-lurp/0123456789ab/obj/App/net10.0/App.AssemblyInfo.cs")]
    public async Task Compare_RedirectedDiagnostics_ReportDifferentIdentity(string otherPath)
    {
        var (snapshot, copy) = await IndexAndCopyAsync();
        AddDiagnostic(DbPath, snapshot, "/tmp/cache-a/lurp/0123456789ab/obj/App/net10.0/App.AssemblyInfo.cs");
        AddDiagnostic(copy, snapshot, otherPath);

        Assert.Contains(SnapshotComparison.Compare(DbPath, snapshot, copy, snapshot),
            difference => difference.StartsWith("diagnostics:", StringComparison.Ordinal));
    }

    private static void AddDiagnostic(string dbPath, string snapshot, string path)
    {
        using var connection = TestDatabase.Open(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostics (snapshot_id, project_name, document_path, severity, id, message)
            VALUES (@snapshot, 'App', @path, 'Hidden', 'CS8019', 'Unnecessary using directive.');
            """;
        command.Parameters.AddWithValue("@snapshot", snapshot);
        command.Parameters.AddWithValue("@path", path);
        command.ExecuteNonQuery();
    }

    private async Task<(string Snapshot, string Copy)> IndexAndCopyAsync()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Calc.cs"] = "namespace App;\n\npublic class Calc<T>\n{\n    public int Add(int a, int b) => a + b;\n}\n"
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        var copy = Path.Combine(TestDir, "copy.db");
        SqliteConnection.ClearAllPools();
        File.Copy(DbPath, copy);
        return (snapshot, copy);
    }
}
