using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     D26: the MS.DI registration forms, against the real Microsoft.Extensions.DependencyInjection,
///     Options and Hosting assemblies of the test host. Each expected edge is derived by hand from the
///     MS.DI documentation of the called method (service type, implementation type, factory, instance)
///     and from D8 (the owner is the member that runs the call). Symbols are named by their
///     documentation-comment id (C# specification, annex D); <c>runtime:unknown</c> is the
///     runtime-unknown node.
/// </summary>
public sealed class DependencyInjectionRegistrationTests : InMemoryTestBase
{
    private const string FrameworkDerived = Provenance.FrameworkDerived;
    private const string RuntimeUnknown = Provenance.RuntimeUnknown;
    private const string Unknown = "runtime:unknown";
    private const string Collection = "Microsoft.Extensions.DependencyInjection.IServiceCollection";

    private const string Types = """
                                 using System;
                                 using Microsoft.Extensions.DependencyInjection;
                                 using Microsoft.Extensions.DependencyInjection.Extensions;
                                 using Microsoft.Extensions.Hosting;

                                 namespace N;

                                 public interface IA { } public class A : IA { }
                                 public interface IB { } public class B : IB { }
                                 public interface IC { } public class C : IC { }
                                 public interface ID { } public class D : ID { }
                                 public interface IE { } public class E : IE { }
                                 public interface IF { } public class F : IF { }
                                 public interface IG { } public class G : IG { }
                                 public interface IH { } public class H : IH { }
                                 public interface IK { } public class K : IK { }
                                 public interface IL { } public class L : IL { }
                                 public interface IM { } public class M : IM { }
                                 public interface IS { } public class S : IS { }
                                 public interface IT { } public class T1 : IT { }
                                 public class Self { }
                                 public interface IRepo<T> { } public class Repo<T> : IRepo<T> { }
                                 public class Opts { }
                                 public class Widget { public void Configure() { } }
                                 public class Worker : IHostedService
                                 {
                                     public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
                                     public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken token) => System.Threading.Tasks.Task.CompletedTask;
                                 }

                                 """;

    /// <summary>
    ///     S02-F4, S02-F5: every MS.DI form with a static service and implementation gives
    ///     service → implementation and owner → implementation (D26). A registration without an
    ///     implementation registers the service type itself, so service → service is a fact (D134).
    /// </summary>
    [Fact]
    public async Task StaticRegistrations_GiveServiceAndOwnerEdges()
    {
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            ["Reg.cs"] = Types + """
                                 public static class Reg
                                 {
                                     public static void Explicit(IServiceCollection s) => s.AddScoped<IA, A>();
                                     public static void Try(IServiceCollection s) => s.TryAddScoped<IB, B>();
                                     public static void Keyed(IServiceCollection s) => s.AddKeyedScoped<IC, C>("k");
                                     public static void Conditional(IServiceCollection? s) => s?.AddScoped<IF, F>();
                                     public static void Named(IServiceCollection s) => s.AddScoped(implementationType: typeof(G), serviceType: typeof(IG));
                                     public static void SelfRegistration(IServiceCollection s) => s.AddSingleton<Self>();
                                     public static void OpenGeneric(IServiceCollection s) => s.AddScoped(typeof(IRepo<>), typeof(Repo<>));
                                     public static void Descriptor(IServiceCollection s) => s.Add(ServiceDescriptor.Transient<IS, S>());
                                     public static void DescriptorConstructor(IServiceCollection s) => s.Replace(new ServiceDescriptor(typeof(IT), typeof(T1), ServiceLifetime.Singleton));
                                 }
                                 """
        }, runAdapters: true);

        Assert.Equal(Sorted(
            ($"M:N.Reg.Explicit({Collection})", "T:N.A", FrameworkDerived), ("T:N.IA", "T:N.A", FrameworkDerived),
            ($"M:N.Reg.Try({Collection})", "T:N.B", FrameworkDerived), ("T:N.IB", "T:N.B", FrameworkDerived),
            ($"M:N.Reg.Keyed({Collection})", "T:N.C", FrameworkDerived), ("T:N.IC", "T:N.C", FrameworkDerived),
            ($"M:N.Reg.Conditional({Collection})", "T:N.F", FrameworkDerived), ("T:N.IF", "T:N.F", FrameworkDerived),
            ($"M:N.Reg.Named({Collection})", "T:N.G", FrameworkDerived), ("T:N.IG", "T:N.G", FrameworkDerived),
            ($"M:N.Reg.SelfRegistration({Collection})", "T:N.Self", FrameworkDerived), ("T:N.Self", "T:N.Self", FrameworkDerived),
            ($"M:N.Reg.OpenGeneric({Collection})", "T:N.Repo`1", FrameworkDerived), ("T:N.IRepo`1", "T:N.Repo`1", FrameworkDerived),
            ($"M:N.Reg.Descriptor({Collection})", "T:N.S", FrameworkDerived), ("T:N.IS", "T:N.S", FrameworkDerived),
            ($"M:N.Reg.DescriptorConstructor({Collection})", "T:N.T1", FrameworkDerived), ("T:N.IT", "T:N.T1", FrameworkDerived)),
            Registers(extraction));
    }

    /// <summary>
    ///     S02-F5, D26, D146: a factory, an instance whose static type does not name the run-time type,
    ///     a <c>Type</c> value that is not a <c>typeof</c> and a descriptor that is not built in place
    ///     give no implementation claim. The service → runtime-unknown edge is written when the service
    ///     is static, the owner → runtime-unknown edge when it is not.
    /// </summary>
    [Fact]
    public async Task UnreadableImplementations_GiveRuntimeUnknown()
    {
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            ["Reg.cs"] = Types + """
                                 public abstract class AbstractG : IG { }

                                 public static class Reg
                                 {
                                     public static void Factory(IServiceCollection s) => s.AddScoped<ID>(_ => new D());
                                     public static void Instance(IServiceCollection s) => s.AddSingleton<IE>(new E());
                                     public static void InstanceOfInterface(IServiceCollection s, IH h) => s.AddSingleton(h);
                                     public static void InstanceOfAbstractClass(IServiceCollection s, AbstractG g) => s.AddSingleton<IG>(g);
                                     public static void TypeVariable(IServiceCollection s, Type t) => s.AddScoped(typeof(IK), t);
                                     public static void UnknownService(IServiceCollection s, Type t) => s.AddScoped(t);
                                     public static void DescriptorVariable(IServiceCollection s, ServiceDescriptor d) => s.TryAdd(d);
                                 }
                                 """
        }, runAdapters: true);

        Assert.Equal(Sorted(
            ("T:N.ID", Unknown, RuntimeUnknown),
            ($"M:N.Reg.Instance({Collection})", "T:N.E", FrameworkDerived), ("T:N.IE", "T:N.E", FrameworkDerived),
            ("T:N.IG", Unknown, RuntimeUnknown),
            ("T:N.IH", Unknown, RuntimeUnknown),
            ("T:N.IK", Unknown, RuntimeUnknown),
            ($"M:N.Reg.UnknownService({Collection},System.Type)", Unknown, RuntimeUnknown),
            ($"M:N.Reg.DescriptorVariable({Collection},Microsoft.Extensions.DependencyInjection.ServiceDescriptor)", Unknown, RuntimeUnknown)),
            Registers(extraction));
    }

    /// <summary>
    ///     S02-F8, D8: the owner of a registration is the member that runs it: a constructor, the field
    ///     whose initializer holds the call, and the method that holds a lambda with the call.
    /// </summary>
    [Fact]
    public async Task RegistrationOwners_FollowD8()
    {
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            ["Reg.cs"] = Types + """
                                 public class Installer
                                 {
                                     public Installer(IServiceCollection s) { s.AddScoped<IH, H>(); }
                                 }

                                 public static class Holder
                                 {
                                     public static readonly IServiceCollection Services = new ServiceCollection().AddScoped<IL, L>();

                                     public static void Lambda(IServiceCollection s)
                                     {
                                         Action register = () => s.AddScoped<IM, M>();
                                         register();
                                     }
                                 }
                                 """
        }, runAdapters: true);

        Assert.Equal(Sorted(
            ($"M:N.Installer.#ctor({Collection})", "T:N.H", FrameworkDerived), ("T:N.IH", "T:N.H", FrameworkDerived),
            ("F:N.Holder.Services", "T:N.L", FrameworkDerived), ("T:N.IL", "T:N.L", FrameworkDerived),
            ($"M:N.Holder.Lambda({Collection})", "T:N.M", FrameworkDerived), ("T:N.IM", "T:N.M", FrameworkDerived)),
            Registers(extraction));
    }

    /// <summary>
    ///     S02-F4, S02-F6, D26: a method that only has a registration name gives nothing; an MS.DI
    ///     method that registers nothing gives nothing; an external method that takes the collection
    ///     gives owner → runtime-unknown (<c>di_external_extension</c>); a hosted service and an
    ///     options registration give owner → runtime-unknown and owner → the named type, both
    ///     <c>runtime_unknown</c> (<c>di_hosted_service</c>, <c>di_options</c>).
    /// </summary>
    [Fact]
    public async Task OtherCalls_GiveOnlyTheirDeclaredBoundaryEdges()
    {
        var extraction = await ExtractAsync(new Dictionary<string, string>
        {
            ["Reg.cs"] = Types + """
                                 public static class Reg
                                 {
                                     public static void NotDi() => new Widget().Configure();
                                     public static void Build(IServiceCollection s) { s.BuildServiceProvider(); s.RemoveAll<IA>(); }
                                     public static void External(IServiceCollection s) => s.AddLogging();
                                     public static void Hosted(IServiceCollection s) => s.AddHostedService<Worker>();
                                     public static void Options(IServiceCollection s) => s.AddOptions<Opts>().Configure(o => { });
                                     public static void ConfigureOptions(IServiceCollection s) => s.Configure<Opts>(o => { });
                                 }
                                 """
        }, runAdapters: true);

        Assert.Equal(Sorted(
            ($"M:N.Reg.External({Collection})", Unknown, RuntimeUnknown),
            ($"M:N.Reg.Hosted({Collection})", Unknown, RuntimeUnknown), ($"M:N.Reg.Hosted({Collection})", "T:N.Worker", RuntimeUnknown),
            ($"M:N.Reg.Options({Collection})", Unknown, RuntimeUnknown), ($"M:N.Reg.Options({Collection})", "T:N.Opts", RuntimeUnknown),
            ($"M:N.Reg.ConfigureOptions({Collection})", Unknown, RuntimeUnknown), ($"M:N.Reg.ConfigureOptions({Collection})", "T:N.Opts", RuntimeUnknown)),
            Registers(extraction));
    }

    private static List<(string Source, string Target, string Provenance)> Sorted(params (string Source, string Target, string Provenance)[] edges)
    {
        return [.. edges.Order()];
    }

    /// <summary>The distinct Registers edges of the extraction, each endpoint as its documentation-comment id.</summary>
    private static List<(string Source, string Target, string Provenance)> Registers(Extraction extraction)
    {
        return [.. extraction.Result.Edges
            .Where(edge => edge.Kind == nameof(EdgeKind.Registers))
            .Select(edge => (DocId(edge.SourceSymbolId), DocId(edge.TargetSymbolId), edge.Provenance))
            .Distinct()
            .Order()];

        static string DocId(string id) => id.IndexOf('|', StringComparison.Ordinal) is var bar and >= 0 ? id[..bar] : id;
    }
}
