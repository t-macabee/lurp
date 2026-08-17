using Lurp.Mcp;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests.Mcp;

public sealed class McpPinningTests : IntegrationTestBase
{
    [Fact]
    public async Task Pin_DoesNotMove_AfterSecondIndexRunMutatesDb()
    {
        // Index initial fixture
        CreateProject("PinnedProj", new Dictionary<string, string>
        {
            ["A.cs"] = "namespace PinnedProj { public class A { public void Foo() {} } }"
        });
        var snapshot1 = await RunFullIndexAsync(DbPath);
        Assert.False(string.IsNullOrEmpty(snapshot1));

        // Open a long-lived session (pinned snapshot)
        var outputDir = Path.GetDirectoryName(SolutionPath)!;
        var sessionArgs = new[] { $"--solution={SolutionPath}" };

        await using var session = McpSessionContext.Create(sessionArgs);
        var pinned = session.PinnedSnapshotId;
        Assert.Equal(snapshot1, pinned);

        // Mutate DB via second index run (add new file) — simulates concurrent writer.
        // Adding a new project ensures a new snapshot_id is generated.
        CreateProject("PinnedProj2", new Dictionary<string, string>
        {
            ["B.cs"] = "namespace PinnedProj2 { public class B { public void Bar() {} } }"
        });

        var snapshot2 = await RunFullIndexNoDeleteAsync(DbPath);
        Assert.NotEqual(snapshot1, snapshot2);

        // Session pin must not have moved.
        Assert.Equal(snapshot1, session.PinnedSnapshotId);
        Assert.Equal(pinned, session.PinnedSnapshotId);

        // Verify store still readable via pinned snapshot (query_only=ON must allow reads).
        var result = session.Store.GetSymbolIdsInSnapshot(pinned);
        Assert.NotEmpty(result);
    }
}
