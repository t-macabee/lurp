using Lurp.Adapters;
using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     Scrutor scans (D28) against a restored Scrutor 4.2.2. Expected pairs come from running
///     Scrutor 4.2.2 on the same types (P2-S07 plan, "Scrutor oracle").
/// </summary>
public sealed class DependencyInjectionScanTests : IntegrationTestBase
{
    /// <summary>
    ///     S02-F7, D27: the explicit registration and the scan give the same relation IJ → J. The
    ///     store keeps the highest provenance rank (D6), so the relation is framework_derived, also
    ///     when the scan comes first in the file.
    /// </summary>
    [Fact]
    public async Task ExplicitRegistration_KeepsFrameworkDerived_WhenAScanBeforeItGivesTheSameRelation()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = """
                                      using Microsoft.Extensions.DependencyInjection;

                                      namespace App;

                                      public interface IJ { }
                                      public class J : IJ { }

                                      public static class Registration
                                      {
                                          public static void Configure(IServiceCollection services)
                                          {
                                              services.Scan(scan => scan.FromAssemblyOf<J>().AddClasses().AsImplementedInterfaces().WithScopedLifetime());
                                              services.AddScoped<IJ, J>();
                                          }
                                      }
                                      """
            },
            ["Scrutor@4.2.2"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var interfaceId = ResolveSymbolId(snapshotId, "global::App.IJ");
        var implementationId = ResolveSymbolId(snapshotId, "global::App.J");
        using var store = OpenStore(DbPath);
        var edge = Assert.Single(store.GetEdges(snapshotId),
            e => e.Kind == nameof(EdgeKind.Registers) && e.SourceSymbolId == interfaceId && e.TargetSymbolId == implementationId);
        Assert.Equal(Provenance.FrameworkDerived, edge.Provenance);
    }
}
