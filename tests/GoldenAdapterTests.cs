using Lurp.Shared;
using Lurp.Storage;
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
}

/// <summary>Pattern B host for the Serialization adapter golden test.</summary>
public sealed class SerializationAdapterExtractorTest : InMemoryTestBase
{
}
