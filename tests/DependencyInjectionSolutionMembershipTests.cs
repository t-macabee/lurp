using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     L20: a solution membership rule for DI extensions. An <c>IServiceCollection</c> extension
///     method of another project in the solution is not external to the solution, so its caller
///     keeps the compiler-proved call and gets no <c>runtime:unknown</c> claim. A metadata reference
///     is still external. The sibling body keeps its own claim, because the type parameter that it
///     registers is open.
/// </summary>
public sealed class DependencyInjectionSolutionMembershipTests : IntegrationTestBase
{
    private const string AspNetCore = "Microsoft.AspNetCore.App";

    [Fact]
    public async Task SolutionExtensionMethods_AreNotExternal_AndMetadataExtensionsStillAre()
    {
        CreateProject("Lib", new Dictionary<string, string>
        {
            ["LibExtensions.cs"] = """
                                   using Microsoft.Extensions.DependencyInjection;

                                   namespace Lib
                                   {
                                       public class LibService { }
                                   }

                                   namespace Microsoft.Extensions.DependencyInjection
                                   {
                                       public static class LibServiceCollectionExtensions
                                       {
                                           public static IServiceCollection AddLib<T>(this IServiceCollection services)
                                               where T : class
                                               => services.AddScoped<T>();
                                       }
                                   }
                                   """
        }, frameworkReferences: [AspNetCore]);

        CreateProject("App", new Dictionary<string, string>
        {
            ["OwnExtensions.cs"] = """
                                   using Microsoft.Extensions.DependencyInjection;

                                   namespace Microsoft.Extensions.DependencyInjection
                                   {
                                       public static class AppServiceCollectionExtensions
                                       {
                                           public static IServiceCollection AddOwn<T>(this IServiceCollection services)
                                               where T : class
                                               => services;
                                       }
                                   }
                                   """,
            ["Caller.cs"] = """
                            using Lib;
                            using Microsoft.Extensions.DependencyInjection;

                            namespace App;

                            public class AppService { }

                            public static class Caller
                            {
                                public static void Sibling(IServiceCollection services) =>
                                    services.AddLib<AppService>();

                                public static void Own(IServiceCollection services) =>
                                    services.AddOwn<AppService>();

                                public static void External(IServiceCollection services) =>
                                    services.AddLogging();
                            }
                            """
        }, frameworkReferences: [AspNetCore], projectReferences: ["Lib"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var siblingCallerId = ResolveSymbolId(snapshotId, "global::App.Caller.Sibling");
        var ownCallerId = ResolveSymbolId(snapshotId, "global::App.Caller.Own");
        var externalCallerId = ResolveSymbolId(snapshotId, "global::App.Caller.External");
        var siblingBodyId = ResolveSymbolId(
            snapshotId, "global::Microsoft.Extensions.DependencyInjection.LibServiceCollectionExtensions.AddLib<T>");

        using var store = OpenStore(DbPath);
        var claims = store.GetEdges(snapshotId)
            .Where(edge => edge.Kind == nameof(EdgeKind.Registers) && edge.Provenance == Provenance.RuntimeUnknown)
            .ToList();

        // A metadata-reference extension is still external to the solution.
        var external = Assert.Single(claims, edge => edge.SourceSymbolId == externalCallerId);
        Assert.Equal(GraphNodeIds.RuntimeUnknown, external.TargetSymbolId);

        // A sibling-project extension and a same-project extension are not external.
        Assert.DoesNotContain(claims, edge => edge.SourceSymbolId == siblingCallerId);
        Assert.DoesNotContain(claims, edge => edge.SourceSymbolId == ownCallerId);

        // The sibling body registers its own open type parameter: that claim is its own.
        var openParameter = Assert.Single(claims, edge => edge.SourceSymbolId == siblingBodyId);
        Assert.Equal(GraphNodeIds.RuntimeUnknown, openParameter.TargetSymbolId);

        Assert.Equal(2, claims.Count);
    }
}
