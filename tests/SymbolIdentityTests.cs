using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
/// Phase 3: symbol IDs must be deterministic and stable across re-indexing,
/// workspace reloads, and incremental passes that touch other files.
/// </summary>
public sealed class SymbolIdentityTests : IntegrationTestBase
{
    private readonly string _secondDbPath;

    public SymbolIdentityTests()
    {
        _secondDbPath = Path.Combine(TestDir, "second.db");
    }

    private sealed record SymbolRow(string SymbolId, string? Fqn, string? MetadataJson);

    private static List<SymbolRow> ReadSymbolRows(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT symbol_id, fqn, metadata_json
            FROM snapshot_symbols
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SymbolRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return result;
    }

    private static Dictionary<string, SymbolRow> ReadSymbolRowsByDocument(string dbPath, string snapshotId, string relativePath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT s.symbol_id, s.fqn, s.metadata_json
            FROM snapshot_symbols s
            JOIN declarations d ON d.symbol_id = s.symbol_id
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE s.snapshot_id = @snapshotId
              AND doc.relative_path = @path
            ORDER BY s.symbol_id;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@path", relativePath);

        var result = new Dictionary<string, SymbolRow>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = new SymbolRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        return result;
    }

    [SkippableFact]
    public async Task SameSource_IndexedTwiceInFreshDbs_ProducesIdenticalSymbolRows()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                    namespace TestProject;

                    public class Calculator
                    {
                        public int Add(int a, int b) => a + b;
                    }
                    """,
            });

        var snapshot1 = await RunFullIndexAsync(DbPath);
        var snapshot2 = await RunFullIndexAsync(_secondDbPath);

        Assert.Equal(snapshot1, snapshot2);
        Assert.Equal(ReadSymbolRows(DbPath, snapshot1), ReadSymbolRows(_secondDbPath, snapshot2));
    }

    [SkippableFact]
    public async Task SolutionReload_NewWorkspace_NewDb_ProducesIdenticalSymbolRows()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                    namespace TestProject;

                    public class Calculator
                    {
                        public int Add(int a, int b) => a + b;
                    }
                    """,
                ["Service.cs"] = """
                    namespace TestProject;

                    public class Service
                    {
                        public int Compute(int x, int y) => new Calculator().Add(x, y);
                    }
                    """,
            });

        // Each full index opens a fresh MSBuildWorkspace internally, so two
        // runs in separate databases exercise the reload path end to end.
        var snapshot1 = await RunFullIndexAsync(DbPath);
        var snapshot2 = await RunFullIndexAsync(_secondDbPath);

        Assert.Equal(ReadSymbolRows(DbPath, snapshot1), ReadSymbolRows(_secondDbPath, snapshot2));
    }

    [SkippableFact]
    public async Task UnchangedFile_KeepsSymbolIdsAcrossIncrementalEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                    namespace TestProject;

                    public class Calculator
                    {
                        public int Add(int a, int b) => a + b;
                        public int Keep(int v) => v;
                    }
                    """,
                ["Service.cs"] = """
                    namespace TestProject;

                    public class Service
                    {
                        public int Compute(int x, int y) => new Calculator().Add(x, y);
                    }
                    """,
            });

        var snapshotA = await RunFullIndexAsync(DbPath);

        // Touch only Service.cs: Calculator.cs symbols must keep their IDs,
        // including the untouched Keep method.
        WriteFile("TestProject", "Service.cs", """
            namespace TestProject;

            public class Service
            {
                public int Compute(int x, int y) => new Calculator().Add(x, y) + 1;
            }
            """);

        var snapshotB = await RunIncrementalIndexAsync();

        var before = ReadSymbolRowsByDocument(DbPath, snapshotA, "src/TestProject/Calculator.cs");
        var after = ReadSymbolRowsByDocument(DbPath, snapshotB, "src/TestProject/Calculator.cs");

        Assert.NotEmpty(before);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (symbolId, row) in before)
            Assert.Equal(row, after[symbolId]);
    }

    [SkippableFact]
    public async Task SameFqnAcrossAssemblies_GetsDistinctSymbolIds()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("AppOne",
            new Dictionary<string, string>
            {
                ["Internal.cs"] = """
                    namespace Shared;

                    internal class Internal
                    {
                    }
                    """,
            });

        CreateProject("AppTwo",
            new Dictionary<string, string>
            {
                ["Internal.cs"] = """
                    namespace Shared;

                    internal class Internal
                    {
                    }
                    """,
            });

        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        var matches = new List<string>();
        try
        {
            foreach (var id in store.GetSymbolIdsInSnapshot(snapshotId))
            {
                var info = store.GetSymbolInfo(id, snapshotId);
                if (info?.FullyQualifiedName == "global::Shared.Internal")
                    matches.Add(id);
            }
        }
        finally
        {
            store.Close();
        }

        Assert.Equal(2, matches.Count);
        Assert.NotEqual(matches[0], matches[1]);
    }
}
