namespace Lurp.Tests;

public sealed class GraphNodeTests : IntegrationTestBase
{
    [Fact]
    public async Task IndexedSolution_RegistersOnlyBoundaryNodesThatAreNotSnapshotSymbols()
    {
        CreateProject("Lib",
            new Dictionary<string, string>
            {
                ["Base.cs"] = "namespace Lib { public class Base { } }"
            });
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["D.cs"] = """
                           namespace App
                           {
                               public class D : Lib.Base { }
                               public class E : System.Exception { }
                           }
                           """
            },
            projectReferences: ["Lib"]);

        var snapshotId = await RunFullIndexAsync(DbPath);
        var libBaseId = ResolveSymbolId(snapshotId, "global::Lib.Base");

        using var conn = OpenDbConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT node_id FROM snapshot_graph_nodes WHERE snapshot_id = @snapshotId;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);

        var nodes = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            nodes.Add(reader.GetString(0));

        Assert.Contains(nodes, n => n.StartsWith("T:System.Exception|", StringComparison.Ordinal));
        Assert.DoesNotContain(libBaseId, nodes);
    }
}
