using Lurp.Shared;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
/// Phase 1 adapter golden tests. Framework-based edges are asserted against a
/// persisted snapshot (Pattern A) except the Serialization edge, whose target is
/// an external type (System.String) and is therefore dropped by the snapshot
/// orphan filter — that one is asserted at extractor level (Pattern B).
/// </summary>
public sealed class GoldenAdapterTests : IntegrationTestBase
{
    private const string AspNetCoreFramework = "Microsoft.AspNetCore.App";

    [SkippableFact]
    public async Task AspNetCoreAdapter_ControllerActionProducesRoutesToDeclaresReturns()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["ItemsController.cs"] = """
                    using Microsoft.AspNetCore.Mvc;

                    namespace App;

                    [Route("api/[controller]")]
                    public class ItemsController : ControllerBase
                    {
                        [HttpGet("{id}")]
                        public Item Get(int id) => new Item();
                    }

                    public class Item
                    {
                        public int Id { get; set; }
                    }
                    """,
            },
            frameworkReferences: [AspNetCoreFramework]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var routes = QueryEdges(snapshotId, "RoutesTo", Provenance.FrameworkDerived);
        var route = Assert.Single(routes);
        Assert.Equal("route://api/[controller]/{id}", route.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.ItemsController.Get"), route.TargetSymbolId);
        Assert.Equal("aspnetcore-v1", route.ExtractorVersion);

        // Node kinds are not round-tripped through the edges read path; the
        // graph_nodes table carries them.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT node_kind FROM graph_nodes WHERE node_id = @nodeId;";
            command.Parameters.AddWithValue("@nodeId", "route://api/[controller]/{id}");
            var actual = command.ExecuteScalar();
            Assert.Equal(GraphNodeKind.Route.ToString(), actual);
        }

        // The adapter's Declares/Returns edges duplicate the compiler-proved
        // workspace edges for the same (source, target, kind) and are merged
        // away by EdgeDedup in favor of the stronger provenance — the persisted
        // snapshot therefore carries the compiler-proved variants.
        var declares = QueryEdges(snapshotId, "Declares", Provenance.CompilerProved);
        Assert.Contains(declares, e =>
            e.SourceSymbolId == ResolveSymbolId(snapshotId, "global::App.ItemsController") &&
            e.TargetSymbolId == ResolveSymbolId(snapshotId, "global::App.ItemsController.Get") &&
            e.ExtractorVersion == "declares-v1");

        var returns = QueryEdges(snapshotId, "Returns", Provenance.CompilerProved);
        Assert.Contains(returns, e =>
            e.SourceSymbolId == ResolveSymbolId(snapshotId, "global::App.ItemsController.Get") &&
            e.TargetSymbolId == ResolveSymbolId(snapshotId, "global::App.Item") &&
            e.ExtractorVersion == "returns-v1");
    }

    [SkippableFact]
    public async Task DependencyInjectionAdapter_AddScopedProducesRegisters()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = """
                    using Microsoft.Extensions.DependencyInjection;

                    namespace App;

                    public interface IService { }
                    public class Service : IService { }

                    public static class Registration
                    {
                        public static void Configure(IServiceCollection services)
                        {
                            services.AddScoped<IService, Service>();
                        }
                    }
                    """,
            },
            frameworkReferences: [AspNetCoreFramework]);

        var snapshotId = await RunFullIndexAsync(DbPath);

        var registers = QueryEdges(snapshotId, "Registers", Provenance.FrameworkDerived);
        Assert.Equal(2, registers.Count);

        // Registration site anchors source to the enclosing method.
        Assert.Contains(registers, e =>
            e.SourceSymbolId == ResolveSymbolId(snapshotId, "global::App.Registration.Configure") &&
            e.TargetSymbolId == ResolveSymbolId(snapshotId, "global::App.Service"));

        // The interface-to-implementation relation is emitted as a second Registers edge.
        Assert.Contains(registers, e =>
            e.SourceSymbolId == ResolveSymbolId(snapshotId, "global::App.IService") &&
            e.TargetSymbolId == ResolveSymbolId(snapshotId, "global::App.Service"));

        foreach (var edge in registers)
            Assert.Equal("di-v1", edge.ExtractorVersion);
    }

    [SkippableFact]
    public async Task DIConventionMatcher_ScanCallProducesConventionRegistersEdge()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = """
                    using Microsoft.Extensions.DependencyInjection;
                    using Scrutor;

                    namespace App;

                    public class App
                    {
                    }

                    public static class Registration
                    {
                        public static void Configure(IServiceCollection services)
                        {
                            services.Scan(scan => scan
                                .FromAssembliesOf<App>()
                                .AddClasses()
                                .AsImplementedInterfaces()
                                .WithScopedLifetime());
                        }
                    }
                    """,
            },
            packageReferences: ["Scrutor@4.2.2"]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var registers = QueryEdges(snapshotId, "Registers", Provenance.Convention);
        var edge = Assert.Single(registers);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Registration.Configure"), edge.SourceSymbolId);
        Assert.StartsWith("convention:assembly_scan:", edge.TargetSymbolId);
        Assert.Equal("di-v1", edge.ExtractorVersion);

        // Node kinds are not round-tripped through the edges read path; the
        // graph_nodes table carries them.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT node_kind FROM graph_nodes WHERE node_id = @nodeId;";
            command.Parameters.AddWithValue("@nodeId", edge.TargetSymbolId);
            var actual = command.ExecuteScalar();
            Assert.Equal(GraphNodeKind.Convention.ToString(), actual);
        }
    }

    [SkippableFact]
    public async Task MediatRAdapter_RequestHandlerProducesHandles()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Ping.cs"] = """
                    using MediatR;

                    namespace App;

                    public class Ping : IRequest<string> { }

                    public class PingHandler : IRequestHandler<Ping, string>
                    {
                        public Task<string> Handle(Ping request, CancellationToken ct) => Task.FromResult("pong");
                    }
                    """,
            },
            packageReferences: ["MediatR@12.4.1"]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var handles = QueryEdges(snapshotId, "Handles", Provenance.FrameworkDerived);
        var edge = Assert.Single(handles);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Ping"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.PingHandler.Handle"), edge.TargetSymbolId);
        Assert.Equal("mediatr-v1", edge.ExtractorVersion);
    }

    [SkippableFact]
    public async Task EfCoreAdapter_DbContextDbSetProducesMapsTo()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Blogging.cs"] = """
                    using Microsoft.EntityFrameworkCore;

                    namespace App;

                    public class Blog
                    {
                        public int Id { get; set; }
                        public string? Name { get; set; }
                    }

                    public class AppDbContext : DbContext
                    {
                        public DbSet<Blog> Blogs { get; set; } = null!;
                    }
                    """,
            },
            packageReferences: ["Microsoft.EntityFrameworkCore@10.0.1"]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var mapsTo = QueryEdges(snapshotId, "MapsTo", Provenance.FrameworkDerived);
        var edge = Assert.Single(mapsTo);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.AppDbContext.Blogs"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Blog"), edge.TargetSymbolId);
        Assert.Equal("efcore-v1", edge.ExtractorVersion);
    }

    [SkippableFact]
    public async Task EfCoreAdapter_HasQueryFilterProducesQueryFilterAnnotation()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Blogging.cs"] = """
                    using Microsoft.EntityFrameworkCore;

                    namespace App;

                    public class Blog
                    {
                        public int Id { get; set; }
                        public bool IsDeleted { get; set; }
                    }

                    public class AppDbContext : DbContext
                    {
                        public DbSet<Blog> Blogs { get; set; } = null!;

                        protected override void OnModelCreating(ModelBuilder modelBuilder)
                        {
                            modelBuilder.Entity<Blog>().HasQueryFilter(b => !b.IsDeleted);
                        }
                    }
                    """,
            },
            packageReferences: ["Microsoft.EntityFrameworkCore@10.0.1"]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var blogId = ResolveSymbolId(snapshotId, "global::App.Blog");

        using (var store = OpenStore(DbPath))
        {
            var constraints = store.GetAnnotations(snapshotId)
                .Where(a => a.Kind == "ef_query_filter_constraint")
                .ToList();

            var annotation = Assert.Single(constraints);
            Assert.Equal(blogId, annotation.SymbolId);
            Assert.Equal("Blog: HasQueryFilter: b => !b.IsDeleted", annotation.Value);
            Assert.EndsWith("Blogging.cs", annotation.DocumentPath);
        }
    }

    [Fact]
    public async Task SerializationAdapter_JsonPropertyNameProducesReferenceEdge()
    {
        // Attribute classification is name-based, so no framework reference is
        // needed to observe the extractor's output. The edge's target (System.String)
        // is external, so the persisted snapshot drops it as external by design —
        // this golden test pins the extractor contract at the extraction level.
        var test = new SerializationAdapterExtractorTest();
        try
        {
            var extraction = await test.ExtractAsync(
                new Dictionary<string, string>
                {
                    ["Product.cs"] = """
                        using System.Text.Json.Serialization;

                        namespace App;

                        public class Product
                        {
                            [JsonPropertyName("product_name")]
                            public string Name { get; set; } = "";
                        }
                        """,
                },
                runAdapters: true);

            var references = extraction.EdgesOf("References", Provenance.FrameworkDerived);
            var edge = Assert.Single(references);
            Assert.Equal(extraction.ResolveId("global::App.Product.Name"), edge.SourceSymbolId);
            Assert.StartsWith("T:System.String|", edge.TargetSymbolId);
            Assert.Equal("serialization-v1", edge.ExtractorVersion);
            Assert.Equal("Product.cs", edge.SourceDocumentPath);
        }
        finally
        {
            test.Dispose();
        }
    }

    [SkippableFact]
    public async Task TestAdapter_TestProjectProducesTestedBy()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                    namespace App;

                    public class Calculator
                    {
                        public int Add(int a, int b) => a + b;
                    }
                    """,
            });

        CreateProject("App.Tests",
            new Dictionary<string, string>
            {
                ["CalculatorTests.cs"] = """
                    using Xunit;

                    public class CalculatorTests
                    {
                        [Fact]
                        public void Add()
                        {
                            new App.Calculator().Add(1, 2);
                        }
                    }
                    """,
            },
            projectReferences: ["App"],
            packageReferences: ["xunit@2.9.3"]);

        var snapshotId = await RunFullIndexAsync(DbPath);

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived);
        var edge = Assert.Single(testedBy);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Calculator"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::CalculatorTests.Add"), edge.TargetSymbolId);
        Assert.Equal("test-v3", edge.ExtractorVersion);
    }

    // ── R5 characterization tests ──────────────────────────────────────

    /// <summary>
    /// Characterization (R5): a non-conventional DI registration
    /// (<c>AddHostedService&lt;T&gt;</c>) produces <see cref="Provenance.RuntimeUnknown"/>
    /// edges, NOT <see cref="Provenance.FrameworkDerived"/>. The adapter detects the
    /// registration but declares the runtime semantics unmodeled.
    /// </summary>
    [SkippableFact]
    public async Task DIAdapter_AddHostedService_ProducesRuntimeUnknown()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Registration.cs"] = """
                    using Microsoft.Extensions.DependencyInjection;
                    using Microsoft.Extensions.Hosting;

                    namespace App;

                    public class MyHostedService : IHostedService
                    {
                        public System.Threading.Tasks.Task StartAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
                        public System.Threading.Tasks.Task StopAsync(System.Threading.CancellationToken ct) => System.Threading.Tasks.Task.CompletedTask;
                    }

                    public static class Registration
                    {
                        public static void Configure(IServiceCollection services)
                        {
                            services.AddHostedService<MyHostedService>();
                        }
                    }
                    """,
            },
            frameworkReferences: [AspNetCoreFramework]);

        var snapshotId = await RunFullIndexAsync(DbPath);

        // Non-conventional → RuntimeUnknown, never FrameworkDerived.
        var runtimeUnknown = QueryEdges(snapshotId, "Registers", Provenance.RuntimeUnknown);
        Assert.NotEmpty(runtimeUnknown);

        var frameworkDerived = QueryEdges(snapshotId, "Registers", Provenance.FrameworkDerived);
        Assert.Empty(frameworkDerived);

        // One of the RuntimeUnknown edges must target the runtime:unknown sentinel.
        Assert.Contains(runtimeUnknown, e => e.TargetSymbolId == GraphNodeIds.RuntimeUnknown);
    }

    /// <summary>
    /// Characterization (R5): MediatR adapter only recognizes
    /// <c>IRequestHandler</c> and <c>INotificationHandler</c>. Other handler
    /// patterns (stream handlers) are silently skipped — no edge is emitted,
    /// and no false "proved" claim is made.
    /// </summary>
    [SkippableFact]
    public async Task MediatRAdapter_StreamHandler_IsSilentlySkipped()
    {
        Skip.If(!MSBuildLocator.IsRegistered, "MSBuild is not available on this system.");

        CreateProject("App",
            new Dictionary<string, string>
            {
                ["StreamPing.cs"] = """
                    using MediatR;

                    namespace App;

                    public class StreamPing : IStreamRequest<string> { }

                    public class StreamPingHandler : IStreamRequestHandler<StreamPing, string>
                    {
                        public IAsyncEnumerable<string> Handle(StreamPing request, CancellationToken ct)
                            => AsyncEnumerable.Empty<string>();
                    }
                    """,
            },
            packageReferences: ["MediatR@12.4.1"]);

        await RestoreSolutionAsync();
        var snapshotId = await RunFullIndexAsync(DbPath);

        // Stream handler pattern is not recognized — zero Handles edges.
        var handles = QueryEdges(snapshotId, "Handles", Provenance.FrameworkDerived);
        Assert.Empty(handles);

        // Confirm no false Registers claim either.
        var registers = QueryEdges(snapshotId, "Registers", Provenance.FrameworkDerived);
        Assert.Empty(registers);
    }

    /// <summary>
    /// Characterization (R5): <see cref="DeclaredBoundaries.Known"/> registry
    /// contains exactly the expected entries. This is a lightweight staleness
    /// check — if a new boundary is added or removed, this test forces a
    /// conscious update.
    /// </summary>
    [Fact]
    public void DeclaredBoundaries_RegistryContainsExpectedEntries()
    {
        var expectedIds = new[]
        {
            "di_hosted_service",
            "di_options",
            "di_external_extension",
            "masstransit_consumer",
            "ef_convention",
            "shape_similarity",
        };

        var actualIds = DeclaredBoundaries.Known.Select(e => e.Id).OrderBy(id => id).ToList();
        var expected = expectedIds.OrderBy(id => id).ToList();

        Assert.Equal(expected, actualIds);
    }
}

/// <summary>Pattern B host for the Serialization adapter golden test.</summary>
public sealed class SerializationAdapterExtractorTest : InMemoryTestBase
{
}
