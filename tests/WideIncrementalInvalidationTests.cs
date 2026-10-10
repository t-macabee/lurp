using Lurp.Storage;
using Microsoft.Build.Locator;
using System.Globalization;
using System.Text;

namespace Lurp.Tests;

/// <summary>
///     B16: an edit to one document invalidates every document of its project, so an
///     incremental run can send more than SQLite's 32,766-parameter limit to the FTS rebuild,
///     the maintenance deletes and the semantic diff. This end-to-end test indexes a project
///     wide enough that the incremental run must bind more symbol ids than that limit.
/// </summary>
[Trait("Category", "Slow")]
public sealed class WideIncrementalInvalidationTests : IntegrationTestBase
{
    [SkippableFact]
    public async Task IncrementalRun_InvalidatingMoreSymbolsThanTheParameterLimit_CompletesWithFullSearchIndex()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        var files = new Dictionary<string, string>();
        var sb = new StringBuilder();
        for (var file = 0; file < 8; file++)
        {
            sb.Clear();
            sb.AppendLine("namespace WideProbe;");
            sb.AppendLine();
            for (var type = 0; type < 100; type++)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"internal class C{file}_{type}");
                sb.AppendLine("{");
                for (var method = 0; method < 50; method++)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    internal void M{method}() {{ }}");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            files[$"Gen{file}.cs"] = sb.ToString();
        }

        files["Edited.cs"] = """
            namespace WideProbe;

            internal class Edited
            {
                internal void A() { }
            }
            """;
        CreateProject("WideProbe", files);

        var fullSnapshotId = await RunFullIndexAsync(DbPath);

        // Precondition: the fixture must exceed SQLite's 32,766-parameter limit on its own.
        using (var connection = OpenDbConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @snapshotId;";
            command.Parameters.AddWithValue("@snapshotId", fullSnapshotId);
            Assert.True((long)command.ExecuteScalar()! > 32_766, "fixture too small to exceed the parameter limit");
        }

        // A real code change in one file invalidates every document of its project.
        WriteFile("WideProbe", "Edited.cs", """
            namespace WideProbe;

            internal class Edited
            {
                internal void A() { }
                internal void B() { }
            }
            """);
        Touch(Path.Combine(TestDir, "src", "WideProbe", "Edited.cs"));

        var newSnapshotId = await RunIncrementalIndexAsync();

        // The run took the incremental path and finished, rather than falling back to a full index.
        Assert.NotEqual(fullSnapshotId, newSnapshotId);
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.ChangeScope));
        Assert.Equal(1L, CountStepRows(newSnapshotId, SnapshotTimingSteps.FtsBuild));

        var addedSymbolId = ResolveSymbolId(newSnapshotId, "global::WideProbe.Edited.B");

        long ftsCount;
        long fqnCount;
        using (var connection = OpenDbConnection())
        {
            using var ftsCommand = connection.CreateCommand();
            ftsCommand.CommandText = "SELECT COUNT(*) FROM symbol_fts WHERE snapshot_id = @snapshotId;";
            ftsCommand.Parameters.AddWithValue("@snapshotId", newSnapshotId);
            ftsCount = (long)ftsCommand.ExecuteScalar()!;

            using var symbolCommand = connection.CreateCommand();
            symbolCommand.CommandText =
                "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @snapshotId AND fqn IS NOT NULL;";
            symbolCommand.Parameters.AddWithValue("@snapshotId", newSnapshotId);
            fqnCount = (long)symbolCommand.ExecuteScalar()!;

            using var rowCommand = connection.CreateCommand();
            rowCommand.CommandText =
                "SELECT COUNT(*) FROM symbol_fts WHERE snapshot_id = @snapshotId AND symbol_id = @symbolId;";
            rowCommand.Parameters.AddWithValue("@snapshotId", newSnapshotId);
            rowCommand.Parameters.AddWithValue("@symbolId", addedSymbolId);
            Assert.Equal(1L, (long)rowCommand.ExecuteScalar()!);
        }

        Assert.True(fqnCount > 32_766, "the incremental snapshot has too few symbols for this probe");
        Assert.Equal(fqnCount, ftsCount);

        using var store = OpenStore(DbPath);
        var changes = store.GetSemanticChangesToSnapshot(newSnapshotId);
        Assert.Contains(changes, change => change.SymbolId == addedSymbolId && change.ChangeType == ChangeType.SymbolAdded);
    }
}
