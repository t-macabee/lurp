using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public partial class MigrationRunnerTests
{
    public class B5AdapterTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        private static Compilation CreateCompilationWithReferences(string source, string[] refAssemblies, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            var references = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.ComponentModel.INotifyPropertyChanged).Assembly.Location),
            };
            foreach (var asm in refAssemblies)
            {
                try { references.Add(MetadataReference.CreateFromFile(asm)); }
                catch { }
            }
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                references);
        }

        private static readonly string MediatRStubs = @"
namespace MediatR {
    public interface IRequest<TResponse> { }
    public interface IRequestHandler<TRequest, TResponse> where TRequest : IRequest<TResponse> { }
    public interface INotification { }
    public interface INotificationHandler<TNotification> where TNotification : INotification { }
}";

        private static readonly string AspNetCoreMvcStubs = @"
namespace Microsoft.AspNetCore.Mvc {
    public class ControllerBase {
        public IActionResult Ok() => null!;
        public IActionResult Ok(object value) => null!;
    }
    public class RouteAttribute : System.Attribute {
        public RouteAttribute(string template) { }
    }
    public class HttpGetAttribute : System.Attribute {
        public HttpGetAttribute(string template) { }
    }
    public class HttpPostAttribute : System.Attribute {
        public HttpPostAttribute() { }
    }
    public class FromBodyAttribute : System.Attribute { }
    public interface IActionResult { }
}";

        private static readonly string DependencyInjectionStubs = @"
namespace Microsoft.Extensions.DependencyInjection {
    public interface IServiceCollection { }
    public static class ServiceCollectionServiceExtensions {
        public static IServiceCollection AddScoped<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
        public static IServiceCollection AddTransient<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
        public static IServiceCollection AddSingleton<TService, TImplementation>(this IServiceCollection services) where TImplementation : TService => services;
    }
}";

        private static readonly string ScrutorStubs = @"
namespace Scrutor {
    public interface ITypeSourceSelector { }
    public interface IImplementationTypeSelector { }
    public interface IServiceTypeSelector { }
    public static class ServiceCollectionExtensions {
        public static Microsoft.Extensions.DependencyInjection.IServiceCollection Scan(
            this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
            System.Action<ITypeSourceSelector> action) => services;
    }
    public static class TypeSourceSelectorExtensions {
        public static IImplementationTypeSelector FromAssembliesOf<T>(this ITypeSourceSelector source) => null!;
        public static IImplementationTypeSelector FromAssembliesOf(this ITypeSourceSelector source, params System.Type[] types) => null!;
        public static IImplementationTypeSelector AddClasses(this IImplementationTypeSelector source) => null!;
        public static IImplementationTypeSelector AddClasses(this IImplementationTypeSelector source, System.Action<IImplementationTypeFilter> filter) => null!;
        public static IServiceTypeSelector AsImplementedInterfaces(this IImplementationTypeSelector source) => null!;
        public static ITypeSourceSelector AsMatchingInterface(this IImplementationTypeSelector source) => null!;
    }
    public static class ServiceTypeSelectorExtensions {
        public static Microsoft.Extensions.DependencyInjection.IServiceCollection WithScopedLifetime(this IServiceTypeSelector source) => null!;
    }
    public interface IImplementationTypeFilter {
        IImplementationTypeFilter AssignableTo<T>();
    }
}";

        private static readonly string EfCoreStubs = @"
namespace Microsoft.EntityFrameworkCore {
    public class DbContext { }
    public class DbSet<TEntity> where TEntity : class { }
}";

        private static readonly string XunitStubs = @"
namespace Xunit {
    public class FactAttribute : System.Attribute { }
}";

        private static Compilation CreateCompilationWithStubs(string source, string stubs, string assemblyName = "TestAssembly")
        {
            var testTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var stubsTree = CSharpSyntaxTree.ParseText(stubs, path: "stubs.cs");

            return CSharpCompilation.Create(
                assemblyName,
                [stubsTree, testTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }


        private static EdgeLocationResolver CreateTestLocationResolver()
        {
            return new EdgeLocationResolver(
                Array.Empty<string>(),
                Array.Empty<string>(),
                ".");
        }

        private static AdapterExtractionContext CreateAdapterContext(Compilation compilation, string snapshotId)
            => new(compilation, snapshotId, CreateTestLocationResolver(), null, new Dictionary<SyntaxTree, SemanticModel>());

        private static MetadataReference EmitStubAssembly(string assemblyName, string stubSource)
        {
            var stubTree = CSharpSyntaxTree.ParseText(stubSource, path: $"{assemblyName}.cs");
            var compilation = CSharpCompilation.Create(
                assemblyName,
                [stubTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var ms = new System.IO.MemoryStream();
            var emitResult = compilation.Emit(ms);
            if (!emitResult.Success)
                throw new InvalidOperationException(
                    $"{assemblyName} stub assembly emission failed: " +
                    string.Join("; ", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            ms.Position = 0;
            return MetadataReference.CreateFromStream(ms);
        }

        private static Compilation CreateCompilationWithMediatR(string source)
        {
            var testTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var mediatrRef = EmitStubAssembly("MediatR", MediatRStubs);

            return CSharpCompilation.Create(
                "TestAssembly",
                [testTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    mediatrRef,
                ]);
        }

        private static Compilation CreateCompilationWithEfCore(string source)
        {
            var testTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var efCoreRef = EmitStubAssembly("Microsoft.EntityFrameworkCore", EfCoreStubs);

            return CSharpCompilation.Create(
                "TestAssembly",
                [testTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    efCoreRef,
                ]);
        }

        [Fact]
        public void AspNetCore_RouteAttribute_EmitsRoutesToEdge()
        {
            var source = @"
using Microsoft.AspNetCore.Mvc;

[Route(""api/users"")]
public class UsersController : ControllerBase
{
    [HttpGet(""{id}"")]
    public IActionResult GetUser(int id) => Ok();
}
";

            var compilation = CreateCompilationWithStubs(source, AspNetCoreMvcStubs);
            var adapter = new AspNetCoreAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-aspnet-001")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "RoutesTo");

        }

        [Fact]
        public void AspNetCore_HttpPostAttribute_EmitsRoutesToEdge()
        {
            var source = @"
using Microsoft.AspNetCore.Mvc;

[Route(""api/orders"")]
public class OrdersController : ControllerBase
{
    [HttpPost]
    public IActionResult CreateOrder([FromBody] object order) => Ok();
}
";
            var compilation = CreateCompilationWithStubs(source, AspNetCoreMvcStubs);
            var adapter = new AspNetCoreAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-aspnet-003")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "RoutesTo");

        }

        [Fact]
        public void AspNetCore_NoController_EmitsZeroEdges()
        {
            var source = @"
public class PlainClass
{
    public void DoSomething() { }
}
";
            var compilation = CreateCompilation(source);
            var adapter = new AspNetCoreAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-aspnet-002")).Edges;

            Assert.Empty(edges);
        }

        [Fact]
        public void DI_AddScoped_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-001")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId.Contains("Service"));

        }

        [Fact]
        public void DI_AddTransient_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-003")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId.Contains("Service"));
        }

        [Fact]
        public void DI_AddSingleton_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-004")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Registers" && e.TargetSymbolId.Contains("Service"));
        }

        [Fact]
        public void DI_ScrutorScan_FromAssembliesOf_EmitsConventionRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssembliesOf<IService>()
            .AddClasses()
            .AsImplementedInterfaces());
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs + ScrutorStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-scrutor-001")).Edges;

            var edge = Assert.Single(edges, e => e.Kind == "Registers");
            Assert.Equal(Provenance.Convention, edge.Provenance);
            Assert.NotEqual(Provenance.CompilerProved, edge.Provenance);
            Assert.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, edge.TargetSymbolId);
            Assert.Equal(GraphNodeKind.Convention, edge.TargetNodeKind);
            Assert.Contains("TestAssembly", edge.TargetSymbolId, StringComparison.Ordinal);
        }

        [Fact]
        public void DI_ScrutorScan_FromAssembliesOfTypeOf_EmitsConventionRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

public class Service { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssembliesOf(typeof(Service))
            .AddClasses()
            .AsImplementedInterfaces());
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs + ScrutorStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-scrutor-002")).Edges;

            var edge = Assert.Single(edges, e => e.Kind == "Registers");
            Assert.Equal(Provenance.Convention, edge.Provenance);
            Assert.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, edge.TargetSymbolId);
            Assert.Equal(GraphNodeKind.Convention, edge.TargetNodeKind);
        }

        [Fact]
        public void DI_ExplicitGeneric_NotCompilerProved()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-005")).Edges;

            var registrations = edges.Where(e => e.Kind == "Registers").ToList();
            Assert.Equal(2, registrations.Count);

            var ownerEdge = Assert.Single(registrations, e => e.SourceSymbolId.Contains("ConfigureServices"));
            Assert.Equal(Provenance.FrameworkDerived, ownerEdge.Provenance);
            Assert.NotEqual(Provenance.CompilerProved, ownerEdge.Provenance);
            Assert.False(ownerEdge.TargetSymbolId.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, StringComparison.Ordinal));
        }

        [Fact]
        public void DI_AddScoped_InterfaceToImplementation_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-010")).Edges;

            var ifaceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("IService")!, "TestAssembly");
            var serviceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("Service")!, "TestAssembly");

            var registration = Assert.Single(edges,
                e => e.Kind == "Registers" && e.SourceSymbolId == ifaceId && e.TargetSymbolId == serviceId);

            Assert.Equal(Provenance.FrameworkDerived, registration.Provenance);
            Assert.Equal("di-v1", registration.ExtractorVersion);
            Assert.Equal("test.cs", registration.SourceDocumentPath);
            Assert.False(registration.IsCrossGenerated);
            Assert.False(registration.SourceSymbolId.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, StringComparison.Ordinal));
        }

        [Fact]
        public void DI_AddTransient_InterfaceToImplementation_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddTransient<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-011")).Edges;

            var ifaceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("IService")!, "TestAssembly");
            var serviceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("Service")!, "TestAssembly");

            var registration = Assert.Single(edges,
                e => e.Kind == "Registers" && e.SourceSymbolId == ifaceId && e.TargetSymbolId == serviceId);

            Assert.Equal(Provenance.FrameworkDerived, registration.Provenance);
            Assert.Equal("di-v1", registration.ExtractorVersion);
            Assert.Equal("test.cs", registration.SourceDocumentPath);
            Assert.False(registration.IsCrossGenerated);
        }

        [Fact]
        public void DI_AddSingleton_InterfaceToImplementation_EmitsRegistersEdge()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-012")).Edges;

            var ifaceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("IService")!, "TestAssembly");
            var serviceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("Service")!, "TestAssembly");

            var registration = Assert.Single(edges,
                e => e.Kind == "Registers" && e.SourceSymbolId == ifaceId && e.TargetSymbolId == serviceId);

            Assert.Equal(Provenance.FrameworkDerived, registration.Provenance);
            Assert.Equal("di-v1", registration.ExtractorVersion);
            Assert.Equal("test.cs", registration.SourceDocumentPath);
            Assert.False(registration.IsCrossGenerated);
        }

        [Fact]
        public void DI_ConstructorParameter_AndExplicitRegistration_FormReferenceRegistrationPath()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;

public interface IService { }
public class Service : IService { }

public class Consumer
{
    public Consumer(IService service) { }
}

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<IService, Service>();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs);

            var docVersions = new Dictionary<DocumentId, DocumentVersionId>
            {
                { new DocumentId("test.cs"), DocumentVersionId.Compute("test-content") },
                { new DocumentId("stubs.cs"), DocumentVersionId.Compute("stub-content") },
            };

            var memberEdges = new MemberEdgeExtractor(
                compilation, docVersions, new HashSet<DocumentId>(), "snap-b5-di-path-001", "/").ExtractAll();
            var adapterEdges = new DependencyInjectionAdapter().Extract(
                CreateAdapterContext(compilation, "snap-b5-di-path-001")).Edges;
            var edges = memberEdges.Concat(adapterEdges).ToList();

            var consumerCtor = compilation.GetTypeByMetadataName("Consumer")!.InstanceConstructors.Single();
            var iface = compilation.GetTypeByMetadataName("IService")!;
            var service = compilation.GetTypeByMetadataName("Service")!;

            var consumerId = SymbolIdFactory.Make(consumerCtor, "TestAssembly")!;
            var ifaceId = SymbolIdFactory.Make(iface, "TestAssembly")!;
            var serviceId = SymbolIdFactory.Make(service, "TestAssembly")!;

            var reference = Assert.Single(edges,
                e => e.Kind == "References" && e.SourceSymbolId == consumerId && e.TargetSymbolId == ifaceId);
            Assert.Equal(ExtractorConstants.ParameterDependenciesExtractor, reference.ExtractorVersion);

            var registration = Assert.Single(edges,
                e => e.Kind == "Registers" && e.SourceSymbolId == ifaceId && e.TargetSymbolId == serviceId);
            Assert.Equal(Provenance.FrameworkDerived, registration.Provenance);

            var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-di-path-{Guid.NewGuid():N}.db");
            using var store = new SqliteIndexStore(dbPath);
            try
            {
                store.Open();
                store.RunMigrations();
                store.SaveEdges("snap-b5-di-path-001", edges);

                var traverser = new ImpactTraverser(store, "snap-b5-di-path-001");
                var paths = traverser.TraceImpact(
                    consumerId, ImpactDirection.Downstream, allowedEdgeKinds: ["References", "Registers"]);

                var path = Assert.Single(paths);
                Assert.False(path.Truncated);
                Assert.Equal(2, path.TotalSteps);
                Assert.Equal(consumerId, path.Hops[0].SourceSymbolId);
                Assert.Equal(ifaceId, path.Hops[0].TargetSymbolId);
                Assert.Equal("References", path.Hops[0].EdgeKind);
                Assert.Equal(ifaceId, path.Hops[1].SourceSymbolId);
                Assert.Equal(serviceId, path.Hops[1].TargetSymbolId);
                Assert.Equal("Registers", path.Hops[1].EdgeKind);
            }
            finally
            {
                store.Close();
                if (File.Exists(dbPath)) File.Delete(dbPath);
            }
        }

        [Fact]
        public void DI_Scrutor_AsImplementedInterfaces_EmitsConventionRegistrationEdges()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

public interface IService { }
public interface IOtherService { }
public class Service : IService, IOtherService { }
public abstract class AbstractService : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.Scan(scan => scan
            .FromAssembliesOf<IService>()
            .AddClasses()
            .AsImplementedInterfaces()
            .WithScopedLifetime());
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs + ScrutorStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-scrutor-003")).Edges;

            Assert.Contains(edges,
                e => e.Kind == "Registers" && e.TargetSymbolId.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix));

            var ifaceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("IService")!, "TestAssembly");
            var otherId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("IOtherService")!, "TestAssembly");
            var serviceId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("Service")!, "TestAssembly");
            var abstractId = SymbolIdFactory.Make(compilation.GetTypeByMetadataName("AbstractService")!, "TestAssembly");

            var registrations = edges
                .Where(e => e.Kind == "Registers" && !e.TargetSymbolId.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix))
                .ToList();

            Assert.Equal(2, registrations.Count);
            Assert.Contains(registrations, e => e.SourceSymbolId == ifaceId && e.TargetSymbolId == serviceId);
            Assert.Contains(registrations, e => e.SourceSymbolId == otherId && e.TargetSymbolId == serviceId);
            Assert.DoesNotContain(registrations, e => e.TargetSymbolId == abstractId);
            Assert.All(registrations, e => Assert.Equal(Provenance.Convention, e.Provenance));
            Assert.All(registrations, e => Assert.Equal("di-v1", e.ExtractorVersion));
            Assert.All(registrations, e => Assert.Equal("test.cs", e.SourceDocumentPath));
            Assert.All(registrations, e => Assert.False(e.IsCrossGenerated));
        }

        [Fact]
        public void DI_Scrutor_UnsupportedPattern_RetainsOnlyConventionPlaceholder()
        {
            var source = @"
using Microsoft.Extensions.DependencyInjection;
using Scrutor;

public interface IService { }
public class Service : IService { }

public class Startup
{
    public void ConfigureServices(IServiceCollection services, System.Type[] types)
    {
        services.Scan(scan => scan
            .FromAssembliesOf(types)
            .AddClasses()
            .AsImplementedInterfaces()
            .WithScopedLifetime());
    }
}
";
            var compilation = CreateCompilationWithStubs(source, DependencyInjectionStubs + ScrutorStubs);
            var adapter = new DependencyInjectionAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-di-scrutor-004")).Edges;

            var edge = Assert.Single(edges, e => e.Kind == "Registers");
            Assert.Equal(Provenance.Convention, edge.Provenance);
            Assert.StartsWith(GraphNodeIds.AssemblyScanConventionPrefix, edge.TargetSymbolId);
            Assert.Equal(GraphNodeKind.Convention, edge.TargetNodeKind);
        }

        [Fact]
        public void MediatR_INotificationHandler_EmitsHandlesEdge()
        {
            var source = @"
using MediatR;

public class UserCreatedEvent : INotification { }
public class UserCreatedHandler : INotificationHandler<UserCreatedEvent>
{
    public void Handle(UserCreatedEvent notification) { }
}
";
            var compilation = CreateCompilationWithMediatR(source);
            var adapter = new MediatRAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-mediatr-003")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Handles" && e.Provenance == "framework_derived");
            Assert.Contains(edges, e => e.TargetSymbolId.Contains("Handle") && e.SourceSymbolId.Contains("UserCreatedEvent"));
        }

        [Fact]
        public void MediatR_RequestHandler_EmitsHandlesEdge()
        {
            var source = @"
using MediatR;

public class GetUserQuery : IRequest<string> { }
public class GetUserHandler : IRequestHandler<GetUserQuery, string>
{
    public string Handle(GetUserQuery request) => ""ok"";
}
";
            var compilation = CreateCompilationWithMediatR(source);
            var adapter = new MediatRAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-mediatr-001")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "Handles" && e.Provenance == "framework_derived");
            Assert.Contains(edges, e => e.TargetSymbolId.Contains("Handle") && e.SourceSymbolId.Contains("GetUserQuery"));
        }

        [Fact]
        public void MediatR_NoReferences_EmitsZeroEdges()
        {
            var source = @"
public class Plain { }
";
            var compilation = CreateCompilation(source);
            var adapter = new MediatRAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-mediatr-002")).Edges;

            Assert.Empty(edges);
        }

        [Fact]
        public void EfCore_DbSet_EmitsMapsToEdge()
        {
            var source = @"
using Microsoft.EntityFrameworkCore;

public class User { }
public class AppDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
}
";
            var compilation = CreateCompilationWithEfCore(source);
            var adapter = new EfCoreAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-ef-001")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "MapsTo" && e.TargetSymbolId.Contains("User"));

        }

        [Fact]
        public void Serialization_JsonPropertyName_EmitsEdge()
        {
            var source = @"
using System.Text.Json.Serialization;

public class UserProfile
{
    [JsonPropertyName(""email_address"")]
    public string Email { get; set; }
}
";
            var compilation = CreateCompilation(source);
            var adapter = new SerializationAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-serial-001")).Edges;

            var emailEdges = edges.Where(e =>
                e.Kind == "References" &&
                e.SourceSymbolId.Contains("Email")).ToList();

            Assert.NotEmpty(emailEdges);
        }

        [Fact]
        public void Serialization_NoAttributes_EmitsZeroEdges()
        {
            var source = @"
public class Plain
{
    public string Name { get; set; }
}
";
            var compilation = CreateCompilation(source);
            var adapter = new SerializationAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-serial-002")).Edges;

            Assert.Empty(edges);
        }

        [Fact]
        public void TestAdapter_FactMethod_EmitsTestedByEdge()
        {
            var source = @"
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public class BarTests
{
    [Fact]
    public void Foo_UsesStringBuilder()
    {
        var x = new System.Text.StringBuilder();
    }
}
";
            var compilation = CreateCompilationWithStubs(source, XunitStubs, "MyProject.Tests");
            var adapter = new TestAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-test-001")).Edges;

            Assert.NotEmpty(edges);
            Assert.Contains(edges, e => e.Kind == "TestedBy" && e.Provenance == "framework_derived");
            Assert.Contains(edges, e => e.SourceSymbolId.Contains("StringBuilder") && e.TargetSymbolId.Contains("Foo_UsesStringBuilder"));
        }

        [Fact]
        public void TestAdapter_NonTestProject_EmitsZeroEdges()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(@"
public class Foo
{
    public void Bar() { }
}
", path: "test.cs");

            var compilation = CSharpCompilation.Create(
                "MyProject",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);

            var adapter = new TestAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-test-002")).Edges;

            Assert.Empty(edges);
        }

        [Fact]
        public void TestAdapter_HelperConstructedService_InvokedInTestBody_EmitsTestedByEdge()
        {
            var productionRef = EmitStubAssembly("MyApp.Production", @"
public class CourseEnrollmentService
{
    public void EnrollAsync() { }
}");

            var testTree = CSharpSyntaxTree.ParseText(@"
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public class CourseEnrollmentServiceTests
{
    [Fact]
    public void Enroll_ConstructsViaHelper_ThenInvokesService()
    {
        var service = CreateService();
        service.EnrollAsync();
    }

    private static CourseEnrollmentService CreateService()
    {
        return new CourseEnrollmentService();
    }
}
", path: "test.cs");

            var compilation = CSharpCompilation.Create(
                "MyProject.Tests",
                [testTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    productionRef,
                ]);

            var adapter = new TestAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-test-helper-001")).Edges;

            Assert.Contains(edges, e => e.Kind == "TestedBy"
                && e.SourceSymbolId.Contains("CourseEnrollmentService")
                && e.TargetSymbolId.Contains("Enroll_ConstructsViaHelper_ThenInvokesService"));
        }

        [Fact]
        public void TestAdapter_HelperConstructedService_OnlyConstructedInHelper_EmitsNoTestedByEdge()
        {
            var productionRef = EmitStubAssembly("MyApp.Production", @"
public class CourseEnrollmentService
{
    public void EnrollAsync() { }
}");

            var testTree = CSharpSyntaxTree.ParseText(@"
[AttributeUsage(AttributeTargets.Method)]
public sealed class FactAttribute : Attribute { }

public class CourseEnrollmentServiceTests
{
    [Fact]
    public void Enroll_ConstructsViaHelper_ButBodyDoesNotReferenceService()
    {
        CreateService();
    }

    private static CourseEnrollmentService CreateService()
    {
        return new CourseEnrollmentService();
    }
}
", path: "test.cs");

            var compilation = CSharpCompilation.Create(
                "MyProject.Tests",
                [testTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    productionRef,
                ]);

            var adapter = new TestAdapter();
            var edges = adapter.Extract(CreateAdapterContext(compilation, "snap-b5-test-helper-002")).Edges;

            Assert.DoesNotContain(edges, e => e.Kind == "TestedBy" && e.SourceSymbolId.Contains("CourseEnrollmentService"));
        }

        [Fact]
        public void EdgeRecord_FullConstructor_RoundTrips()
        {
            var edge = new EdgeRecord
            {
                SourceSymbolId = "T:Ns.Foo|asm1",
                TargetSymbolId = "T:Ns.Bar|asm1",
                Kind = "RoutesTo",
                Provenance = "framework_derived",
                SnapshotId = "snap-b5-edge-001",
                ExtractorVersion = "aspnetcore-v1",
                SourceDocumentPath = "src/Test.cs",
                SourceStartLine = 10,
                SourceStartColumn = 5,
                SourceEndLine = 10,
                SourceEndColumn = 20,
                ReceiverTypeConstraintsJson = "[[\"T:Ns.IReceiver|asm1\"]]",
            };

            Assert.Equal("RoutesTo", edge.Kind);
            Assert.Equal("framework_derived", edge.Provenance);
            Assert.Equal("snap-b5-edge-001", edge.SnapshotId);
            Assert.Equal("aspnetcore-v1", edge.ExtractorVersion);
            Assert.Equal("src/Test.cs", edge.SourceDocumentPath);
            Assert.Equal(10, edge.SourceStartLine);
            Assert.Equal("[[\"T:Ns.IReceiver|asm1\"]]", edge.ReceiverTypeConstraintsJson);
        }
    }
}
