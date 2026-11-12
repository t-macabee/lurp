using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     D13: a recursive call is a self Calls edge. The readers ignore a self edge: the capsule
///     does not list the anchor as its own neighbour, and impact does not reach the anchor.
/// </summary>
public sealed class SelfEdgeReaderTests : IntegrationTestBase
{
    private const string Source = """
                                  namespace TestProject;

                                  public class Walker
                                  {
                                      public virtual int Depth(int n) => n <= 0 ? Leaf() : Depth(n - 1);

                                      public int Leaf() => 0;

                                      public int Start() => Depth(3);
                                  }
                                  """;

    [Fact]
    public async Task Capsule_RecursiveAnchor_IsNotItsOwnNeighbour()
    {
        CreateProject("TestProject", new Dictionary<string, string> { ["Walker.cs"] = Source });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var anchorId = ResolveSymbolId(snapshotId, "global::TestProject.Walker.Depth");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 2);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.Equal(["global::TestProject.Walker.Leaf"], capsule.DirectCallees.Select(item => item.FullyQualifiedName));
            Assert.Equal(["global::TestProject.Walker.Start"], capsule.DirectCallers.Select(item => item.FullyQualifiedName));
            foreach (var tier in new[] { capsule.DirectCallers, capsule.DirectCallees, capsule.SecondDegreeContext })
                Assert.DoesNotContain(tier, item => item.SymbolId == anchorId);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    ///     D134, D26: a registration without an implementation registers the service type itself, so
    ///     the type has a self Registers edge. The capsule lists the registering member and never the
    ///     anchor as its own registration.
    /// </summary>
    [Fact]
    public async Task Capsule_SelfRegisteredType_IsNotItsOwnRegistration()
    {
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Setup.cs"] = """
                           using Microsoft.Extensions.DependencyInjection;

                           namespace TestProject;

                           public class Self { }

                           public static class Setup
                           {
                               public static void Register(IServiceCollection services) => services.AddSingleton<Self>();
                           }
                           """
        }, frameworkReferences: ["Microsoft.AspNetCore.App"]);
        var snapshotId = await RunFullIndexAsync(DbPath);
        var anchorId = ResolveSymbolId(snapshotId, "global::TestProject.Self");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 2);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.Equal(["global::TestProject.Setup.Register"], capsule.RegisteredImplementations.Select(item => item.FullyQualifiedName));
        }
        finally
        {
            store.Close();
        }
    }
}
