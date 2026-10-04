using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
///     REGRESSION test for the context tiers' handling of <c>MethodGroupRef</c>
///     edges: a method used only as a method group (<c>xs.Select(F)</c>, event
///     <c>+= F</c>) has no <c>Calls</c> edge, so the direct_callers and
///     relevant_tests tiers must traverse <c>MethodGroupRef</c> as a caller
///     relation. Before the fix both tiers ignored the edge kind and the capsule
///     for F reported no caller.
/// </summary>
public sealed class MethodGroupRefContextTierTests : IntegrationTestBase
{
    private const string ProductionSource = """
                                            namespace TestProject;

                                            public delegate int ChangedHandler(int value);

                                            public sealed class EventSource
                                            {
                                                public event ChangedHandler? Changed;
                                            }

                                            public static class MethodGroupTargets
                                            {
                                                public static int F(int value) => value;

                                                public static int Run(EventSource source, int[] values)
                                                {
                                                    source.Changed += F;
                                                    return values.Select(F).Sum();
                                                }
                                            }
                                            """;

    private const string TestSource = """
                                      using Xunit;

                                      namespace TestProject.Tests;

                                      public sealed class MethodGroupTargetTests
                                      {
                                          [Fact]
                                          public void Run_IsCallable()
                                          {
                                              var source = new TestProject.EventSource();
                                              Assert.Equal(6, TestProject.MethodGroupTargets.Run(source, new[] { 1, 2, 3 }));
                                          }
                                      }
                                      """;

    [SkippableFact]
    public async Task MethodGroupOnlyCallers_AppearInDirectCallersAndRelevantTests()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("TestProject", new Dictionary<string, string> { ["MethodGroupTargets.cs"] = ProductionSource });
        CreateProject("TestProject.Tests",
            new Dictionary<string, string> { ["MethodGroupTargetTests.cs"] = TestSource },
            projectReferences: ["TestProject"],
            packageReferences: ["xunit@2.9.3"]);
        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var anchorId = ResolveSymbolId(snapshotId, "global::TestProject.MethodGroupTargets.F");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 2);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.Contains(capsule.DirectCallers,
                item => item.FullyQualifiedName == "global::TestProject.MethodGroupTargets.Run");
            Assert.Contains(capsule.RelevantTests,
                item => item.FullyQualifiedName.Contains("MethodGroupTargetTests.Run_IsCallable", StringComparison.Ordinal));
        }
        finally
        {
            store.Close();
        }
    }
}
