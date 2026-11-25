using Lurp.Adapters;
using Lurp.Storage;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Tests;

public sealed partial class GoldenAdapterTests
{
    [Fact]
    public async Task Phase3_EfCore_MapsInheritedAndDiscoveredEntities_AndConverges()
    {
        const string entities = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            namespace App;
            public static class BuilderExtensions
            {
                public static EntityTypeBuilder Mapped(this EntityTypeBuilder builder, string table) => builder;
            }
            public class Root
            {
                public int Id { get; set; }
                public Nav Navigation { get; set; } = null!;
                public Address Address { get; set; } = null!;
                public List<Tag> Tags { get; set; } = [];
                public Money Price { get; set; } = null!;
                public OwnedDetail Detail { get; set; } = null!;
                public ReadOnlyDetail ReadOnlyOwned { get; } = new();
                public LambdaOwned LambdaOwned { get; } = new();
                public LambdaHidden LambdaIgnored { get; set; } = null!;
                [System.ComponentModel.DataAnnotations.Schema.NotMapped]
                public Hidden Skipped { get; set; } = null!;
                public Ignored IgnoreMe { get; set; } = null!;
            }
            public class Nav { public Leaf Child { get; set; } = null!; }
            public class Leaf;
            public class Address;
            public class Tag;
            public class Money;
            [Owned] public class OwnedDetail;
            [Owned] public class ReadOnlyDetail;
            public class LambdaOwned;
            public class LambdaHidden;
            public class Hidden;
            public class Ignored;
            public class TypeOfEntity;
            """;
        CreateProject("App", new Dictionary<string, string>
        {
            ["Entities.cs"] = entities,
            ["BaseContext.cs"] = """
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public abstract class BaseContext : DbContext
                {
                    protected DbSet<Root> Rows { get; set; } = null!;
                    private DbSet<Hidden> HiddenRows { get; set; } = null!;
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        model.Entity(typeof(TypeOfEntity));
                        var root = model.Entity<Root>();
                        root.OwnsOne(x => x.Address);
                        root.OwnsMany<Tag>(nameof(Root.Tags));
                        root.ComplexProperty(x => x.Price);
                        root.Ignore(x => x.IgnoreMe);
                        model.Entity(typeof(Root), e =>
                        {
                            e.Mapped("Roots").Ignore(nameof(Root.LambdaIgnored));
                            e.Mapped("Roots").OwnsOne(nameof(LambdaOwned), nameof(Root.LambdaOwned));
                        });
                    }
                }
                """,
            ["AppContext.cs"] = """
                using Microsoft.EntityFrameworkCore;
                namespace App;
                public class AppContext : BaseContext;
                public class CallingContext : BaseContext
                {
                    protected override void OnModelCreating(ModelBuilder model) => base.OnModelCreating(model);
                }
                public class ReplacingContext : BaseContext
                {
                    protected override void OnModelCreating(ModelBuilder model) { }
                }
                """
        }, ["Microsoft.EntityFrameworkCore@10.0.1"]);

        RestoreSolution();
        var initial = await RunFullIndexAsync(DbPath);
        var mappings = QueryEdges(initial, "MapsTo", Provenance.FrameworkDerived);
        var context = ResolveSymbolId(initial, "global::App.AppContext");
        foreach (var name in new[] { "Root", "Nav", "Leaf", "Address", "Tag", "Money", "OwnedDetail", "ReadOnlyDetail", "LambdaOwned", "TypeOfEntity" })
        {
            var id = ResolveSymbolId(initial, "global::App." + name);
            Assert.Contains(mappings, e => e.SourceSymbolId == context && e.TargetSymbolId == id &&
                e.SourceDocumentPath!.EndsWith("AppContext.cs", StringComparison.Ordinal));
        }

        var entity = ResolveSymbolId(initial, "global::App.TypeOfEntity");
        var caller = ResolveSymbolId(initial, "global::App.CallingContext");
        var replacement = ResolveSymbolId(initial, "global::App.ReplacingContext");
        Assert.Contains(mappings, e => e.SourceSymbolId == caller && e.TargetSymbolId == entity);
        Assert.DoesNotContain(mappings, e => e.SourceSymbolId == replacement && e.TargetSymbolId == entity);

        var rows = ResolveSymbolId(initial, "global::App.BaseContext.Rows");
        Assert.Contains(mappings, e => e.SourceSymbolId == rows &&
            e.SourceDocumentPath!.EndsWith("AppContext.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(mappings, e => e.TargetSymbolId.Contains(":App.Hidden|", StringComparison.Ordinal));
        Assert.DoesNotContain(mappings, e => (e.SourceSymbolId == context || e.SourceSymbolId == caller) &&
            e.TargetSymbolId.Contains(":App.Ignored|", StringComparison.Ordinal));
        Assert.DoesNotContain(mappings, e => (e.SourceSymbolId == context || e.SourceSymbolId == caller) &&
            e.TargetSymbolId.Contains(":App.LambdaHidden|", StringComparison.Ordinal));

        WriteFile("App", "Entities.cs",
            entities.Replace("public Leaf Child { get; set; } = null!;", "public Replacement Child { get; set; } = null!;")
            + "\npublic class Replacement;\n");
        var incremental = await RunIncrementalIndexAsync();
        var cleanDb = DbPath + ".clean";
        var clean = await RunFullIndexAsync(cleanDb);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, incremental, cleanDb, clean);
        var updatedMappings = QueryEdges(incremental, "MapsTo", Provenance.FrameworkDerived);
        Assert.DoesNotContain(updatedMappings, e => e.TargetSymbolId.Contains(":App.Leaf|", StringComparison.Ordinal));
        Assert.Contains(updatedMappings, e => e.TargetSymbolId.Contains(":App.Replacement|", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Phase3_Serialization_RecordsConstructorsAndCallbacks()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Dto.cs"] = """
                using System.Runtime.Serialization;
                namespace App;
                public class Dto
                {
                    [System.Text.Json.Serialization.JsonConstructor] internal Dto(string value) { }
                    [Newtonsoft.Json.JsonConstructor] private Dto(int value) { }
                    [OnSerializing] private void BeforeWrite(StreamingContext context) { }
                    [OnSerialized] private void AfterWrite(StreamingContext context) { }
                    [OnDeserializing] private void BeforeRead(StreamingContext context) { }
                    [OnDeserialized] private void AfterRead(StreamingContext context) { }
                    private void Unused(StreamingContext context) { }
                }
                """
        }, ["Newtonsoft.Json@13.0.3"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var annotations = store.GetAnnotations(snapshot).Where(a => a.Kind == "serialization_contract").ToArray();
        Assert.Equal(6, annotations.Length);
        Assert.All(annotations, a => Assert.Equal("included", a.Value));
        Assert.Equal(2, annotations.Count(a => a.SymbolId.Contains(".#ctor(", StringComparison.Ordinal)));
        foreach (var name in new[] { "BeforeWrite", "AfterWrite", "BeforeRead", "AfterRead" })
            Assert.Contains(annotations, a => a.SymbolId == ResolveSymbolId(snapshot, "global::App.Dto." + name));
    }

    [Fact]
    public async Task Phase3_Serialization_InterfaceCallbacksReceiveIncludedAnnotations()
    {
        var test = new SerializationAdapterExtractorTest();
        try
        {
            var extraction = await test.ExtractAsync(
                new Dictionary<string, string>
                {
                    ["Base.cs"] = """
                                  namespace App;

                                  public class CallbackBase
                                  {
                                      public void OnSerializing() { }
                                      public void OnSerialized() { }
                                      public void OnDeserializing() { }
                                      public void OnDeserialized() { }
                                  }
                                  """,
                    ["Callbacks.cs"] = """
                                       using System.Text.Json.Serialization;

                                       namespace App;

                                       public class ImplicitCallbacks : IJsonOnSerializing, IJsonOnSerialized, IJsonOnDeserializing, IJsonOnDeserialized
                                       {
                                           public void OnSerializing() { }
                                           public void OnSerialized() { }
                                           public void OnDeserializing() { }
                                           public void OnDeserialized() { }
                                       }

                                       public class ExplicitCallbacks : IJsonOnSerializing, IJsonOnSerialized, IJsonOnDeserializing, IJsonOnDeserialized
                                       {
                                           void IJsonOnSerializing.OnSerializing() { }
                                           void IJsonOnSerialized.OnSerialized() { }
                                           void IJsonOnDeserializing.OnDeserializing() { }
                                           void IJsonOnDeserialized.OnDeserialized() { }
                                       }

                                       public class InheritedCallbacks : CallbackBase, IJsonOnSerializing, IJsonOnSerialized, IJsonOnDeserializing, IJsonOnDeserialized
                                       {
                                       }
                                       """
                },
                runAdapters: true);

            var callbacks = extraction.Result.Annotations
                .Where(a => a.Kind == "serialization_contract")
                .Select(a => (Symbol: SymbolId.DocCommentIdOf(a.SymbolId), a.Value, a.DocumentPath))
                .OrderBy(a => a.Symbol, StringComparer.Ordinal)
                .ToList();

            string[] expected =
            [
                "M:App.CallbackBase.OnDeserialized",
                "M:App.CallbackBase.OnDeserializing",
                "M:App.CallbackBase.OnSerialized",
                "M:App.CallbackBase.OnSerializing",
                "M:App.ExplicitCallbacks.System#Text#Json#Serialization#IJsonOnDeserialized#OnDeserialized",
                "M:App.ExplicitCallbacks.System#Text#Json#Serialization#IJsonOnDeserializing#OnDeserializing",
                "M:App.ExplicitCallbacks.System#Text#Json#Serialization#IJsonOnSerialized#OnSerialized",
                "M:App.ExplicitCallbacks.System#Text#Json#Serialization#IJsonOnSerializing#OnSerializing",
                "M:App.ImplicitCallbacks.OnDeserialized",
                "M:App.ImplicitCallbacks.OnDeserializing",
                "M:App.ImplicitCallbacks.OnSerialized",
                "M:App.ImplicitCallbacks.OnSerializing"
            ];

            Assert.Equal(expected, callbacks.Select(a => a.Symbol));
            Assert.All(callbacks, a =>
            {
                Assert.Equal("included", a.Value);
                Assert.EndsWith("Callbacks.cs", a.DocumentPath, StringComparison.Ordinal);
            });
        }
        finally
        {
            test.Dispose();
        }
    }

    [Fact]
    public async Task Phase3_AspNetCore_MapsMiddlewareAndCustomHttpVerbs()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Web.cs"] = """
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.Http;
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.Routing;
                namespace App;
                public static class Boot
                {
                    public static void Generic(IApplicationBuilder app) => app.UseMiddleware<Middleware>();
                    public static void NonGeneric(IApplicationBuilder app) => app.UseMiddleware(typeof(Middleware));
                }
                internal sealed class Middleware
                {
                    public Middleware(RequestDelegate next) { }
                    public Task InvokeAsync(HttpContext context) => Task.CompletedTask;
                }
                public sealed class HttpPurgeAttribute : HttpMethodAttribute
                {
                    private static readonly string[] Verbs = ["PURGE", "BAN"];
                    public HttpPurgeAttribute(string pattern) : base(Verbs, pattern) { }
                }
                public sealed class HttpLinkAttribute(string template) : HttpMethodAttribute(new[] { "LINK" }, template);
                [Route("api/[controller]")]
                public class DemoController : ControllerBase
                {
                    [HttpPurge("cache/{id}")] public int Purge(int id) => id;
                    [HttpLink("link/{id}")] public int Link(int id) => id;
                }
                """
        }, frameworkReferences: [AspNetCoreFramework]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var routes = QueryEdges(snapshot, "RoutesTo", Provenance.FrameworkDerived);
        Assert.Equal(["route://BAN /api/Demo/cache/{id}", "route://LINK /api/Demo/link/{id}", "route://PURGE /api/Demo/cache/{id}"],
            routes.Select(e => e.SourceSymbolId).Order(StringComparer.Ordinal));
        var constructs = QueryEdges(snapshot, "Constructs", Provenance.FrameworkDerived);
        var calls = QueryEdges(snapshot, "Calls", Provenance.FrameworkDerived);
        foreach (var method in new[] { "Generic", "NonGeneric" })
        {
            var owner = ResolveSymbolId(snapshot, "global::App.Boot." + method);
            var middleware = ResolveSymbolId(snapshot, "global::App.Middleware");
            Assert.Contains(constructs, e => e.SourceSymbolId == owner && e.TargetSymbolId == middleware);
            Assert.Contains(calls, e => e.SourceSymbolId == owner &&
                e.TargetSymbolId == ResolveSymbolId(snapshot, "global::App.Middleware.InvokeAsync"));
            Assert.Contains(calls, e => e.SourceSymbolId == owner &&
                e.TargetSymbolId.Contains(":App.Middleware.#ctor(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Phase3_AspNetCore_ExternalHttpVerbAttribute_ReadsArrayConstructorArguments()
    {
        var externalAssembly = EmitExternalHttpVerbAssembly();
        CreateProject("App", new Dictionary<string, string>
        {
            ["Web.cs"] = """
                using Microsoft.AspNetCore.Mvc;
                using External;
                namespace App;
                [Route("api/[controller]")]
                public class DemoController : ControllerBase
                {
                    [CustomVerb(new[] { "PURGE" }, "cache/{id}")]
                    public int Purge(int id) => id;
                }
                """
        }, frameworkReferences: [AspNetCoreFramework], metadataReferences: [externalAssembly]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var purge = ResolveSymbolId(snapshot, "global::App.DemoController.Purge");
        var route = Assert.Single(QueryEdges(snapshot, "RoutesTo", Provenance.FrameworkDerived),
            e => e.TargetSymbolId == purge);
        Assert.Equal("route://PURGE /api/Demo/cache/{id}", route.SourceSymbolId);
    }

    [Fact]
    public async Task Phase3_AspNetCore_ImplicitBaseConstructorSuppliesTemplate()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Web.cs"] = """
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.Routing;
                namespace App;
                public abstract class ImplicitBaseAttribute : HttpMethodAttribute
                {
                    protected ImplicitBaseAttribute() : base(new[] { "GET" }, "implicit-base") { }
                }
                public abstract class OptionalBaseAttribute : HttpMethodAttribute
                {
                    protected OptionalBaseAttribute(string template = "opt") : base(new[] { "GET" }, template) { }
                }
                public abstract class ZeroOrOptionalBaseAttribute : HttpMethodAttribute
                {
                    protected ZeroOrOptionalBaseAttribute() : base(new[] { "GET" }, "zero") { }
                    protected ZeroOrOptionalBaseAttribute(string template = "optional") : base(new[] { "GET" }, template) { }
                }
                public sealed class ExplicitEmptyAttribute : ImplicitBaseAttribute
                {
                    public ExplicitEmptyAttribute() { }
                }
                public sealed class NoConstructorAttribute : ImplicitBaseAttribute;
                public sealed class PrimaryAttribute() : ImplicitBaseAttribute;
                public sealed class OptionalAttribute : OptionalBaseAttribute;
                public sealed class ZeroAttribute : ZeroOrOptionalBaseAttribute;
                [Route("api")]
                public class DemoController : ControllerBase
                {
                    [ExplicitEmpty] public int ExplicitEmpty() => 0;
                    [NoConstructor] public int NoConstructor() => 0;
                    [Primary] public int Primary() => 0;
                    [Optional] public int Optional() => 0;
                    [Zero] public int Zero() => 0;
                }
                """
        }, frameworkReferences: [AspNetCoreFramework]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var routes = QueryEdges(snapshot, "RoutesTo", Provenance.FrameworkDerived);
        var expected = new (string Method, string Route)[]
        {
            ("ExplicitEmpty", "route://GET /api/implicit-base"),
            ("NoConstructor", "route://GET /api/implicit-base"),
            ("Primary", "route://GET /api/implicit-base"),
            ("Optional", "route://GET /api/opt"),
            ("Zero", "route://GET /api/zero")
        };

        foreach (var (method, route) in expected)
        {
            var target = ResolveSymbolId(snapshot, "global::App.DemoController." + method);
            Assert.Contains(routes, e => e.TargetSymbolId == target && e.SourceSymbolId == route);
        }

        Assert.Equal(5, routes.Count);
        Assert.Equal(3, routes.Count(e => e.SourceSymbolId == "route://GET /api/implicit-base"));
    }

    [Fact]
    public async Task Phase3_AspNetCore_ExternalHttpGetSubclass_ReadsTemplateAfterArray()
    {
        var externalAssembly = EmitExternalHttpGetAssembly();
        CreateProject("App", new Dictionary<string, string>
        {
            ["Web.cs"] = """
                using Microsoft.AspNetCore.Mvc;
                using External;
                namespace App;
                [Route("api/[controller]")]
                public class DemoController : ControllerBase
                {
                    [CustomGet(new[] { "IGNORED" }, "cache/{id}")]
                    public int Get(int id) => id;
                }
                """
        }, frameworkReferences: [AspNetCoreFramework], metadataReferences: [externalAssembly]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var get = ResolveSymbolId(snapshot, "global::App.DemoController.Get");
        var route = Assert.Single(QueryEdges(snapshot, "RoutesTo", Provenance.FrameworkDerived),
            e => e.TargetSymbolId == get);
        Assert.Equal("route://GET /api/Demo/cache/{id}", route.SourceSymbolId);
    }

    /// <summary>
    ///     Emits a metadata-only assembly whose custom verb attribute chains an array
    ///     constructor argument into <c>HttpMethodAttribute</c>. The adapter cannot read the
    ///     base initializer (there is no syntax for it), so the fallback must inspect the
    ///     attribute's constructor constants instead of dereferencing an array constant.
    /// </summary>
    private string EmitExternalHttpVerbAssembly()
    {
        var references = PlatformReferencePaths()
            .Concat(AspNetCoreReferenceAssemblyPaths())
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "ExternalVerbs",
            [CSharpSyntaxTree.ParseText("""
                using Microsoft.AspNetCore.Mvc.Routing;
                namespace External;
                public sealed class CustomVerbAttribute(string[] verbs, string template) : HttpMethodAttribute(verbs, template);
                """)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        var path = Path.Combine(TestDir, "ExternalVerbs.dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    /// <summary>
    ///     Emits a metadata-only assembly whose attribute derives from the built-in
    ///     <c>HttpGetAttribute</c> and takes the verbs array before the template. No syntax is
    ///     available for its initializer, so the fallback reads the two constructor constants.
    /// </summary>
    private string EmitExternalHttpGetAssembly()
    {
        var references = PlatformReferencePaths()
            .Concat(AspNetCoreReferenceAssemblyPaths())
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "ExternalHttpGet",
            [CSharpSyntaxTree.ParseText("""
                using Microsoft.AspNetCore.Mvc;
                namespace External;
                public sealed class CustomGetAttribute(string[] verbs, string template) : HttpGetAttribute(template);
                """)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.Empty(errors);
        var path = Path.Combine(TestDir, "ExternalHttpGet.dll");
        var result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static string[] PlatformReferencePaths()
    {
        return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> AspNetCoreReferenceAssemblyPaths()
    {
        var runtimeRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", ".."));
        var packRoot = Path.Combine(runtimeRoot, "packs", "Microsoft.AspNetCore.App.Ref");
        Assert.True(Directory.Exists(packRoot), $"ASP.NET Core targeting pack not found at {packRoot}.");
        var version = Directory.EnumerateDirectories(packRoot)
            .OrderByDescending(directory => Version.TryParse(Path.GetFileName(directory), out var parsed)
                ? parsed
                : new Version())
            .First();
        var targetFramework = Directory.EnumerateDirectories(Path.Combine(version, "ref")).First();
        return Directory.EnumerateFiles(targetFramework, "*.dll");
    }

    [Fact]
    public async Task Phase3_DI_SeparatesAssemblyScansFromTypeClaimsAndOptions()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Services.cs"] = """
                using FluentValidation;
                using Microsoft.Extensions.DependencyInjection;
                namespace App;
                public class Client(HttpClient http);
                public class Options;
                public static class Services
                {
                    public static void Scan(IServiceCollection services)
                        => services.AddValidatorsFromAssembly(typeof(Options).Assembly);
                    public static void ScanGeneric(IServiceCollection services)
                        => services.AddValidatorsFromAssemblyContaining<Options>();
                    public static void ScanNonGeneric(IServiceCollection services)
                        => services.AddValidatorsFromAssemblyContaining(typeof(Options), includeInternalTypes: true);
                    public static void Single(IServiceCollection services) => services.AddHttpClient<Client>();
                    public static void Bind(IServiceCollection services) => services.Configure<Options>(_ => { });
                    public static void Add(IServiceCollection services) => services.AddOptions<Client>();
                }
                """
        }, ["FluentValidation.DependencyInjectionExtensions@12.2.0"], frameworkReferences: [AspNetCoreFramework]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var edges = QueryEdges(snapshot, "Registers", Provenance.RuntimeUnknown);
        foreach (var method in new[] { "Scan", "ScanGeneric", "ScanNonGeneric" })
        {
            var owner = ResolveSymbolId(snapshot, "global::App.Services." + method);
            Assert.Contains(QueryEdges(snapshot, "Registers", Provenance.Convention), e =>
                e.SourceSymbolId == owner && e.TargetSymbolId == GraphNodeIds.AssemblyScanConventionPrefix + SymbolId.Parse(owner).AssemblyIdentity);
        }

        var single = ResolveSymbolId(snapshot, "global::App.Services.Single");
        var client = ResolveSymbolId(snapshot, "global::App.Client");
        Assert.Contains(edges, e => e.SourceSymbolId == single && e.TargetSymbolId == client);
        Assert.DoesNotContain(edges, e => e.SourceSymbolId == single &&
            e.TargetSymbolId.StartsWith("convention:assembly_scan:", StringComparison.Ordinal));
        using var store = OpenStore(DbPath);
        var options = store.GetAnnotations(snapshot).Where(a => a.Kind == "options_binding").ToArray();
        Assert.Equal(2, options.Length);
        Assert.Contains(options, a => a.SymbolId == client && a.Value == "configured");
        Assert.Contains(options, a => a.SymbolId == ResolveSymbolId(snapshot, "global::App.Options") && a.Value == "configured");
    }

    [Fact]
    public async Task Phase3_AnnotationsOfExternalTypes_AreNotStored_AndConverge()
    {
        const string other = "namespace App; public class Other { }";
        CreateProject("App", new Dictionary<string, string>
        {
            ["Services.cs"] = """
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.EntityFrameworkCore;
                using Microsoft.Extensions.DependencyInjection;
                namespace App;
                public class Options;
                public class Entity { public string Name { get; set; } = ""; }
                public static class Services
                {
                    public static void Bind(IServiceCollection services)
                    {
                        services.Configure<Options>(_ => { });
                        services.Configure<MvcOptions>(_ => { });
                    }
                }
                public class AppContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder model)
                    {
                        model.Entity<Entity>().HasIndex(e => e.Name).IsUnique();
                        model.Entity<ProblemDetails>().HasIndex(e => e.Title).IsUnique();
                    }
                }
                """,
            ["Other.cs"] = other
        }, ["Microsoft.EntityFrameworkCore@10.0.1"], frameworkReferences: [AspNetCoreFramework]);
        RestoreSolution();

        var initial = await RunFullIndexAsync(DbPath);
        using (var store = OpenStore(DbPath))
        {
            Assert.Equal(
                [("ef_unique_index_constraint", ResolveSymbolId(initial, "global::App.Entity")),
                    ("options_binding", ResolveSymbolId(initial, "global::App.Options"))],
                store.GetAnnotations(initial).Select(a => (a.Kind, a.SymbolId)).Order());
        }

        WriteFile("App", "Other.cs", other + "\npublic class Added { }\n");
        var incremental = await RunIncrementalIndexAsync();
        var cleanDb = DbPath + ".clean";
        var clean = await RunFullIndexAsync(cleanDb);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, incremental, cleanDb, clean);
    }

    [Fact]
    public async Task Phase3_DI_EfMigrationsAssembly_IsNotAnAssemblyScan()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Services.cs"] = """
                using Microsoft.EntityFrameworkCore;
                using Microsoft.EntityFrameworkCore.Infrastructure;
                using Microsoft.Extensions.DependencyInjection;
                namespace App;
                public class AppContext : DbContext;
                public class ProviderExtension : RelationalOptionsExtension
                {
                    public override DbContextOptionsExtensionInfo Info => null!;
                    public override void ApplyServices(IServiceCollection services) { }
                    protected override RelationalOptionsExtension Clone() => this;
                }
                public class ProviderBuilder(DbContextOptionsBuilder options)
                    : RelationalDbContextOptionsBuilder<ProviderBuilder, ProviderExtension>(options);
                public static class Provider
                {
                    public static DbContextOptionsBuilder UseProvider(this DbContextOptionsBuilder options,
                        Action<ProviderBuilder> configure) => options;
                }
                public static class Services
                {
                    public static void ByName(IServiceCollection services) => services.AddDbContext<AppContext>(options =>
                        options.UseProvider(sql => sql.MigrationsAssembly(typeof(AppContext).Assembly.FullName)));
                    public static void ByAssembly(IServiceCollection services) => services.AddDbContext<AppContext>(options =>
                        options.UseProvider(sql => sql.MigrationsAssembly(typeof(AppContext).Assembly)));
                }
                """
        }, ["Microsoft.EntityFrameworkCore.Relational@10.0.13"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);

        Assert.DoesNotContain(QueryEdges(snapshot, "Registers", Provenance.Convention), e =>
            e.TargetSymbolId.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, StringComparison.Ordinal));
        var claims = QueryEdges(snapshot, "Registers", Provenance.RuntimeUnknown);
        var context = ResolveSymbolId(snapshot, "global::App.AppContext");
        foreach (var method in new[] { "ByName", "ByAssembly" })
        {
            var owner = ResolveSymbolId(snapshot, "global::App.Services." + method);
            Assert.Contains(claims, e => e.SourceSymbolId == owner && e.TargetSymbolId == context);
        }
    }

    [Fact]
    public async Task Phase3_TestAdapter_FollowsDataSourcesAndFixtureSetup()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                namespace App;
                public class Member;
                public class Class;
                public class NUnitData;
                public class MSTestData;
                public class FixtureCtor;
                public class FixtureInit;
                public class CollectionInit;
                """
        });
        const string collectionFixture = """
            using Xunit;
            public class SharedFixture : IAsyncLifetime
            {
                public Task InitializeAsync() { _ = new App.CollectionInit(); return Task.CompletedTask; }
                public Task DisposeAsync() => Task.CompletedTask;
            }
            [CollectionDefinition("shared")] public class Shared : ICollectionFixture<SharedFixture>;
            """;
        CreateProject("Checks", new Dictionary<string, string>
        {
            ["Collection.cs"] = collectionFixture,
            ["Checks.cs"] = """
                using System.Collections;
                using Xunit;
                public class DataRows : IEnumerable<object[]>
                {
                    public IEnumerator<object[]> GetEnumerator() { yield return [new App.Class()]; }
                    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
                }
                public class LocalFixture : IAsyncLifetime
                {
                    public LocalFixture() { _ = new App.FixtureCtor(); }
                    Task IAsyncLifetime.InitializeAsync() { _ = new App.FixtureInit(); return Task.CompletedTask; }
                    public Task DisposeAsync() => Task.CompletedTask;
                }
                [Collection("shared")]
                public class X : IClassFixture<LocalFixture>
                {
                    public static IEnumerable<object[]> Rows => [[new App.Member()]];
                    [Theory, MemberData(nameof(Rows))] public void Member(object _) { }
                    [Theory, ClassData(typeof(DataRows))] public void Class(object _) { }
                }
                public class N
                {
                    public static object[] Rows() => [new App.NUnitData()];
                    [NUnit.Framework.TestCaseSource(nameof(Rows))] public void Test(object _) { }
                }
                [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
                public class M
                {
                    public static IEnumerable<object[]> Rows => [[new App.MSTestData()]];
                    [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
                    [Microsoft.VisualStudio.TestTools.UnitTesting.DynamicData(nameof(Rows))]
                    public void Test(object _) { }
                }
                """
        }, ["xunit@2.9.3", "NUnit@5.0.0", "MSTest.TestFramework@4.5.2"], projectReferences: ["App"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        var edges = QueryEdges(snapshot, "TestedBy", Provenance.FrameworkDerived);
        foreach (var (model, test) in new[]
        {
            ("Member", "X.Member"), ("Class", "X.Class"), ("NUnitData", "N.Test"), ("MSTestData", "M.Test"),
            ("FixtureCtor", "X.Member"), ("FixtureInit", "X.Member"), ("CollectionInit", "X.Class")
        })
        {
            var source = ResolveSymbolId(snapshot, "global::App." + model);
            var target = ResolveSymbolId(snapshot, "global::" + test);
            Assert.Contains(edges, e => e.SourceSymbolId == source && e.TargetSymbolId == target);
        }

        WriteFile("Checks", "Collection.cs", collectionFixture.Replace("new App.CollectionInit()", "new App.Member()"));
        var incremental = await RunIncrementalIndexAsync();
        var cleanDb = DbPath + ".clean";
        var clean = await RunFullIndexAsync(cleanDb);
        SnapshotAssertions.CompareSnapshotsAreEquivalent(DbPath, incremental, cleanDb, clean);
    }
}
