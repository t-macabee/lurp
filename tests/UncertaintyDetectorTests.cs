using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     S01-F18: a capsule holds one uncertainty entry for each uncertain relation. Hand count
///     for the source below: <c>Type.GetType("TestProject.Pb7T")</c> in <c>Pb7U.Use</c> is one reflection
///     sink (one <c>ReflectionTargetUnknown</c> relation) whose literal names one declared type
///     (one <c>ReflectionNameCandidate</c> relation).
/// </summary>
public sealed class UncertaintyDetectorTests : IntegrationTestBase
{
    [Fact]
    public async Task EachReflectionRelation_GivesOneUncertaintyEntry()
    {
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Refl.cs"] = """
                          namespace TestProject;

                          public class Pb7T { }

                          public class Pb7U
                          {
                              public void Use() { System.Type.GetType("TestProject.Pb7T"); }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        var useId = ResolveSymbolId(snapshotId, "global::TestProject.Pb7U.Use");

        using var store = OpenStore(DbPath);
        var capsule = ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshotId, useId, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, 8000, 5),
            store, store, store);

        Assert.NotNull(capsule);
        Assert.Single(capsule.Uncertainties, static u => u.RelationshipKind == "ReflectionTargetUnknown");
        Assert.Single(capsule.Uncertainties, static u => u.RelationshipKind == "ReflectionNameCandidate");
    }

    [Fact]
    public async Task EachRuntimeUnknownAndDispatchRelation_GivesOneEntry()
    {
        CreateProject("TestProject", new Dictionary<string, string>
        {
            ["Setup.cs"] = """
                           using Microsoft.Extensions.DependencyInjection;

                           namespace TestProject;

                           public interface IFoo
                           {
                               void Work();
                           }

                           public class BaseFoo
                           {
                               public void Work() { }
                           }

                           public class Foo : BaseFoo, IFoo { }

                           public static class Setup
                           {
                               public static void Register(IServiceCollection services) =>
                                   services.AddSingleton<IFoo>(_ => new Foo());
                           }
                           """
        }, frameworkReferences: ["Microsoft.AspNetCore.App"]);
        var snapshotId = await RunFullIndexAsync(DbPath);
        var fooInterfaceId = ResolveSymbolId(snapshotId, "global::TestProject.IFoo");

        using var store = OpenStore(DbPath);
        var capsule = ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshotId, fooInterfaceId, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, 8000, 2),
            store, store, store);

        Assert.Single(capsule.Uncertainties, static u => u.RelationshipKind == "Registers");
        Assert.Single(capsule.Uncertainties, static u => u.RelationshipKind == "MayDispatchTo");
    }
}
