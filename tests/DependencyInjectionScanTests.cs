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

    private const string Types = """
                                 using Microsoft.Extensions.DependencyInjection;
                                 using Scrutor;

                                 namespace App;

                                 public interface IA { } public class A : IA { }
                                 internal interface IHidden { } internal class Hidden : IHidden { }
                                 public interface IB { } public abstract class AbstractB : IB { } public class B : AbstractB { }
                                 public static class StaticC { }
                                 public interface IRepo<T> { } public class Repo<T> : IRepo<T> { }
                                 public interface INonGen { } public class Gen<T> : INonGen { }
                                 internal class Outer { public class NestedPublic : IA { } }
                                 public struct S : IA { }

                                 """;

    /// <summary>
    ///     S02-F9, D28: the modeled chain with <c>AddClasses()</c> gives the pairs that Scrutor 4.2.2
    ///     registers (oracle run): public classes only, abstract and static classes and structs
    ///     excluded, inherited interfaces included, a generic class only with an interface of the
    ///     same arity. A transient lifetime gives the same pairs as a scoped one. A modeled chain
    ///     gives no placeholder, and a scan of the project's own assembly records one
    ///     <c>convention_scan</c> site.
    /// </summary>
    [Fact]
    public async Task ModeledScan_GivesThePairsThatScrutorRegisters()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = Types + """
                                              public static class Registration
                                              {
                                                  public static void Configure(IServiceCollection services) =>
                                                      services.Scan(scan => scan.FromAssemblyOf<A>().AddClasses().AsImplementedInterfaces().WithTransientLifetime());
                                              }
                                              """
            },
            ["Scrutor@4.2.2"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        Assert.Equal(Sorted(
                ("T:App.IA", "T:App.A", Provenance.Convention),
                ("T:App.IB", "T:App.B", Provenance.Convention),
                ("T:App.IRepo`1", "T:App.Repo`1", Provenance.Convention),
                ("T:App.IA", "T:App.Outer.NestedPublic", Provenance.Convention)),
            Registers(snapshotId));

        using var store = OpenStore(DbPath);
        var site = Assert.Single(store.GetBindingIncompleteness(snapshotId), row => row.Reason == BindingIncompletenessReason.ConventionScan);
        Assert.Equal(("src/App/Registration.cs", 1), (site.DocumentPath, site.Count));
    }

    /// <summary>S02-F9, D28: <c>AddClasses(publicOnly: false)</c> with no lifetime call also selects internal classes.</summary>
    [Fact]
    public async Task ModeledScan_WithPublicOnlyFalse_AddsInternalClasses()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = Types + """
                                              public static class Registration
                                              {
                                                  public static void Configure(IServiceCollection services) =>
                                                      services.Scan(scan => scan.FromAssemblyOf<A>().AddClasses(publicOnly: false).AsImplementedInterfaces());
                                              }
                                              """
            },
            ["Scrutor@4.2.2"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        Assert.Equal(Sorted(
                ("T:App.IA", "T:App.A", Provenance.Convention),
                ("T:App.IB", "T:App.B", Provenance.Convention),
                ("T:App.IHidden", "T:App.Hidden", Provenance.Convention),
                ("T:App.IRepo`1", "T:App.Repo`1", Provenance.Convention),
                ("T:App.IA", "T:App.Outer.NestedPublic", Provenance.Convention)),
            Registers(snapshotId));
    }

    /// <summary>
    ///     S02-F6, D28: a scan outside the modeled chain gives the placeholder of each assembly that it
    ///     names statically, from the Scan call only (the assembly of <c>ServiceCollection</c> is
    ///     Microsoft.Extensions.DependencyInjection.Abstractions in .NET 10); a scan of an assembly
    ///     that is not known statically gives the runtime-unknown node; a method named <c>Scan</c>
    ///     that is not Scrutor's gives nothing.
    /// </summary>
    [Fact]
    public async Task UnmodeledScans_GivePlaceholders_AndOtherScanMethodsGiveNothing()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = Types + """
                                              public class Scanner { public object Scan(System.Func<object, object> f) => f(this); }

                                              public static class Registration
                                              {
                                                  public static void Predicate(IServiceCollection s) =>
                                                      s.Scan(x => x.FromAssemblyOf<A>().AddClasses(c => c.AssignableTo<IA>()).AsImplementedInterfaces());
                                                  public static void External(IServiceCollection s) =>
                                                      s.Scan(x => x.FromAssemblyOf<ServiceCollection>().AddClasses().AsSelf());
                                                  public static void Calling(IServiceCollection s) =>
                                                      s.Scan(x => x.FromCallingAssembly().AddClasses().AsImplementedInterfaces());
                                                  public static void NotScrutor() => new Scanner().Scan(x => x);
                                              }
                                              """
            },
            ["Scrutor@4.2.2"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        const string collection = "Microsoft.Extensions.DependencyInjection.IServiceCollection";
        Assert.Equal(Sorted(
                ($"M:App.Registration.Predicate({collection})", "convention:assembly_scan:App", Provenance.Convention),
                ($"M:App.Registration.External({collection})", "convention:assembly_scan:Microsoft.Extensions.DependencyInjection.Abstractions", Provenance.Convention),
                ($"M:App.Registration.Calling({collection})", "runtime:unknown", Provenance.RuntimeUnknown)),
            Registers(snapshotId));
    }

    private static List<(string Source, string Target, string Provenance)> Sorted(params (string Source, string Target, string Provenance)[] edges)
    {
        return [.. edges.Order()];
    }

    /// <summary>
    ///     The Registers edges of the snapshot: a symbol as its documentation-comment id, a placeholder
    ///     with the assembly name only (the display name without version, culture and key).
    /// </summary>
    private List<(string Source, string Target, string Provenance)> Registers(string snapshotId)
    {
        using var store = OpenStore(DbPath);
        return [.. store.GetEdges(snapshotId)
            .Where(edge => edge.Kind == nameof(EdgeKind.Registers))
            .Select(edge => (Name(edge.SourceSymbolId), Name(edge.TargetSymbolId), edge.Provenance))
            .Order()];

        static string Name(string id) => id.IndexOfAny(['|', ',']) is var end and >= 0 ? id[..end] : id;
    }
}
