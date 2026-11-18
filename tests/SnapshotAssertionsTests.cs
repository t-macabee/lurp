using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

[Trait("Category", "Slow")]
public sealed class SnapshotAssertionsTests : IntegrationTestBase
{
    [Theory]
    [InlineData("graph_node")]
    [InlineData("project_column")]
    [InlineData("project_reference")]
    [InlineData("annotation_path")]
    public async Task CompareSnapshotsAreEquivalent_FailsOnEachStoredDifference(string differenceKind)
    {
        var (snapshotId, copyPath) = await CreateAndCopyFixtureAsync();

        using (var connection = new SqliteConnection($"Data Source={copyPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            switch (differenceKind)
            {
                case "graph_node":
                    command.CommandText =
                        "INSERT INTO snapshot_graph_nodes (snapshot_id, node_id) VALUES (@snapshotId, 'T:Extra|Extra');";
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    break;
                case "project_column":
                    command.CommandText =
                        "UPDATE projects SET compilation_options_fingerprint = 'changed' WHERE snapshot_id = @snapshotId AND name = 'App';";
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    break;
                case "project_reference":
                    command.CommandText = """
                        DELETE FROM project_references
                        WHERE referenced_project_name = 'Lib'
                          AND project_id IN (SELECT project_id FROM projects WHERE snapshot_id = @snapshotId AND name = 'App');
                        """;
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    break;
                case "annotation_path":
                    command.CommandText =
                        "UPDATE annotations SET document_path = 'src/App/Other.cs' WHERE snapshot_id = @snapshotId;";
                    command.Parameters.AddWithValue("@snapshotId", snapshotId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(differenceKind), differenceKind, null);
            }

            var affected = command.ExecuteNonQuery();
            Assert.True(affected > 0, $"Expected mutation '{differenceKind}' to affect at least one row.");
        }

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotId, copyPath, snapshotId));
    }

    [Fact]
    public async Task CompareSnapshotsAreEquivalent_PassesOnAnUnchangedCopy()
    {
        var (snapshotId, copyPath) = await CreateAndCopyFixtureAsync();

        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, snapshotId, copyPath, snapshotId);
    }

    private async Task<(string SnapshotId, string CopyPath)> CreateAndCopyFixtureAsync()
    {
        CreateProject("Lib", new Dictionary<string, string>
        {
            ["Lib.cs"] = """
                         namespace Lib;

                         public enum Color { Red }
                         """
        });
        CreateProject("App", new Dictionary<string, string>
        {
            ["App.cs"] = """
                         namespace App;

                         public class Use
                         {
                             public Lib.Color C() => Lib.Color.Red;
                         }
                         """
        }, projectReferences: ["Lib"]);

        var snapshotId = await RunFullIndexAsync(DbPath);
        var symbolId = ResolveSymbolId(snapshotId, "global::App.Use.C");

        using (var store = OpenStore(DbPath))
        {
            try
            {
                store.SaveAnnotations(snapshotId,
                [
                    new AnnotationRecord(symbolId, "unmodeled_mediatr_pattern", "X", "src/App/App.cs")
                ]);
            }
            finally
            {
                store.Close();
            }
        }

        SqliteConnection.ClearAllPools();
        var copyPath = Path.Combine(TestDir, "copy.db");
        File.Copy(DbPath, copyPath, overwrite: true);
        return (snapshotId, copyPath);
    }
}
