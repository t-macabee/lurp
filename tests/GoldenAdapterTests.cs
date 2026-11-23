using Lurp.Adapters;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     Phase 1 adapter golden tests. Framework-based edges are asserted against a
///     persisted snapshot (Pattern A) except the Serialization edge, whose target is
///     an external type (System.String) and is therefore dropped by the snapshot
///     orphan filter — that one is asserted at extractor level (Pattern B).
/// </summary>
public sealed class GoldenAdapterTests : IntegrationTestBase
{
    private const string AspNetCoreFramework = "Microsoft.AspNetCore.App";

    private static readonly string[] RouteTargetFqns =
    [
        "global::App.ProbeController.AbstractAction",
        "global::App.ProbeController.PublicInstance"
    ];

    [Fact]
    public async Task AspNetCoreAdapter_ControllerActionProducesRoutesTo()
    {
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
                                         """
            },
            frameworkReferences: [AspNetCoreFramework]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var routes = QueryEdges(snapshotId, "RoutesTo", Provenance.FrameworkDerived);
        var route = Assert.Single(routes);
        Assert.Equal("route://GET /api/Items/{id}", route.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.ItemsController.Get"), route.TargetSymbolId);
        Assert.Equal(new AspNetCoreAdapter().Version, route.ExtractorVersion);
        // Line 7: "    [HttpGet("{id}")]" (col 4) through line 8: "    public Item Get(int id) => new Item();" (col 42).
        Assert.Equal((7, 4, 8, 42),
            (route.SourceStartLine, route.SourceStartColumn, route.SourceEndLine, route.SourceEndColumn));

        // Boundary membership is recorded in snapshot_graph_nodes (D153).
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = @snapshotId AND node_id = @nodeId;";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@nodeId", "route://GET /api/Items/{id}");
            var actual = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1L, actual);
        }
    }

    [Fact]
    public async Task AspNetCoreAdapter_RouteIds_AreTheRoutesAspNetCoreBuilds()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Controllers.cs"] = """
                                     using System.Threading.Tasks;
                                     using Microsoft.AspNetCore.Mvc;

                                     namespace App;

                                     [Route("api/[controller]")]
                                     public class UsersController : ControllerBase
                                     {
                                         [HttpGet("{id}")]
                                         public int Get(int id) => 1;

                                         [HttpGet("a")]
                                         [HttpGet("b")]
                                         public int Two() => 1;

                                         [HttpGet("~/root")]
                                         public int Root() => 1;

                                         [HttpHead("h")]
                                         public int Head() => 1;

                                         public int Plain() => 1;

                                         [AcceptVerbs("get", "POST", Route = "v")]
                                         public int Verbs() => 1;

                                         [HttpPost]
                                         [Route("both")]
                                         public int Both() => 1;

                                         [ActionName("Renamed")]
                                         [HttpGet("[action]")]
                                         public int Named() => 1;

                                         [HttpGet("[action]")]
                                         public Task<int> LoadAsync() => Task.FromResult(1);

                                         [HttpGet("[area]/x")]
                                         public int NoArea() => 1;
                                     }

                                     [Area("Admin")]
                                     [Route("[area]/[controller]")]
                                     public class PanelController : ControllerBase
                                     {
                                         [HttpGet("")]
                                         public int Index() => 1;
                                     }
                                     """
            },
            frameworkReferences: [AspNetCoreFramework]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var routes = QueryEdges(snapshotId, "RoutesTo", Provenance.FrameworkDerived);
        var expectedVersion = new AspNetCoreAdapter().Version;
        Assert.All(routes, r => Assert.Equal(expectedVersion, r.ExtractorVersion));

        var actual = routes
            .Select(r => (r.SourceSymbolId, r.TargetSymbolId))
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToArray();

        var expected = new (string Source, string Target)[]
            {
                ("route://GET /api/Users/{id}", ResolveSymbolId(snapshotId, "global::App.UsersController.Get")),
                ("route://GET /api/Users/a", ResolveSymbolId(snapshotId, "global::App.UsersController.Two")),
                ("route://GET /api/Users/b", ResolveSymbolId(snapshotId, "global::App.UsersController.Two")),
                ("route://GET /root", ResolveSymbolId(snapshotId, "global::App.UsersController.Root")),
                ("route://HEAD /api/Users/h", ResolveSymbolId(snapshotId, "global::App.UsersController.Head")),
                ("route://ANY /api/Users", ResolveSymbolId(snapshotId, "global::App.UsersController.Plain")),
                ("route://GET /api/Users/v", ResolveSymbolId(snapshotId, "global::App.UsersController.Verbs")),
                ("route://POST /api/Users/v", ResolveSymbolId(snapshotId, "global::App.UsersController.Verbs")),
                ("route://POST /api/Users/both", ResolveSymbolId(snapshotId, "global::App.UsersController.Both")),
                ("route://GET /api/Users/Renamed", ResolveSymbolId(snapshotId, "global::App.UsersController.Named")),
                ("route://GET /api/Users/Load", ResolveSymbolId(snapshotId, "global::App.UsersController.LoadAsync")),
                ("route://GET /Admin/Panel", ResolveSymbolId(snapshotId, "global::App.PanelController.Index"))
            }
            .OrderBy(p => p.Source, StringComparer.Ordinal)
            .ThenBy(p => p.Target, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AspNetCoreAdapter_ControllerRuleAndInheritedActions_FollowAspNetCore()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Base.cs"] = """
                              using Microsoft.AspNetCore.Mvc;

                              namespace App;

                              [Route("api/[controller]")]
                              public abstract class ApiBase : ControllerBase
                              {
                                  [HttpGet("ping")]
                                  public int Ping() => 1;
                              }
                              """,
                ["Orders.cs"] = """
                                using Microsoft.AspNetCore.Mvc;

                                namespace App;

                                public class OrdersController : ApiBase
                                {
                                    [HttpGet("list")]
                                    public int List() => 1;
                                }

                                public class CustomersController : ApiBase
                                {
                                }
                                """,
                ["Others.cs"] = """
                                using Microsoft.AspNetCore.Mvc;

                                namespace App.Fake
                                {
                                    public class Controller { }
                                }

                                namespace App
                                {
                                    [Route("poco")]
                                    public class PocoController
                                    {
                                        public int Hello() => 1;
                                    }

                                    [Controller]
                                    [Route("attr")]
                                    public class ByAttr
                                    {
                                        public int Hi() => 1;
                                    }

                                    [NonController]
                                    [Route("nc")]
                                    public class NotOneController : ControllerBase
                                    {
                                        public int X() => 1;
                                    }

                                    [Route("int")]
                                    internal class InternalController : ControllerBase
                                    {
                                        public int I() => 1;
                                    }

                                    [Route("gen")]
                                    public class GenericController<T> : ControllerBase
                                    {
                                        public int G() => 1;
                                    }

                                    public class Outer
                                    {
                                        [Route("n")]
                                        public class NestedController : ControllerBase
                                        {
                                            public int N() => 1;
                                        }
                                    }

                                    [Route("fake")]
                                    public class FakeUser : App.Fake.Controller
                                    {
                                        public int F() => 1;
                                    }
                                }
                                """
            },
            frameworkReferences: [AspNetCoreFramework]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var routes = QueryEdges(snapshotId, "RoutesTo", Provenance.FrameworkDerived);
        var actual = routes
            .Select(r => (r.SourceSymbolId, r.TargetSymbolId))
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToArray();

        var expected = new (string Source, string Target)[]
            {
                ("route://GET /api/Orders/list", ResolveSymbolId(snapshotId, "global::App.OrdersController.List")),
                ("route://GET /api/Orders/ping", ResolveSymbolId(snapshotId, "global::App.ApiBase.Ping")),
                ("route://GET /api/Customers/ping", ResolveSymbolId(snapshotId, "global::App.ApiBase.Ping")),
                ("route://ANY /poco", ResolveSymbolId(snapshotId, "global::App.PocoController.Hello")),
                ("route://ANY /attr", ResolveSymbolId(snapshotId, "global::App.ByAttr.Hi"))
            }
            .OrderBy(p => p.Source, StringComparer.Ordinal)
            .ThenBy(p => p.Target, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);

        var ordersPing = Assert.Single(routes, r => r.SourceSymbolId == "route://GET /api/Orders/ping");
        var customersPing = Assert.Single(routes, r => r.SourceSymbolId == "route://GET /api/Customers/ping");
        Assert.EndsWith("Orders.cs", ordersPing.SourceDocumentPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Orders.cs", customersPing.SourceDocumentPath, StringComparison.OrdinalIgnoreCase);
    }

    // Regression: a class-level [Route] used to give every ordinary method a RoutesTo
    // edge, including private helpers and protected overrides. The expected set follows
    // ASP.NET Core 10's DefaultApplicationModelProvider.IsAction for the same methods.
    [Fact]
    public async Task AspNetCoreAdapter_OnlyActionMethodsGetRoutesTo()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["ProbeController.cs"] = """
                                         using System;
                                         using Microsoft.AspNetCore.Mvc;

                                         namespace App;

                                         public abstract class BaseProbe : ControllerBase
                                         {
                                             protected abstract int GetId();
                                             public abstract int AbstractAction();
                                             [NonAction] public virtual int NonActionOnBase() => 1;
                                         }

                                         [Route("api/probe")]
                                         public class ProbeController : BaseProbe, IDisposable
                                         {
                                             public int PublicInstance() => 1;
                                             public static int PublicStatic() => 1;
                                             private static bool PrivateStatic() => true;
                                             protected override int GetId() => 1;
                                             public override int AbstractAction() => 1;
                                             public override int NonActionOnBase() => 2;
                                             [NonAction] public int NonActionMethod() => 1;
                                             public int Generic<T>() => 1;
                                             public override string ToString() => "";
                                             public void Dispose() { }
                                             internal int InternalMethod() => 1;
                                         }
                                         """
            },
            frameworkReferences: [AspNetCoreFramework]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var routed = QueryEdges(snapshotId, "RoutesTo", Provenance.FrameworkDerived)
            .Select(e => e.TargetSymbolId)
            .Order(StringComparer.Ordinal)
            .ToList();
        var expected = RouteTargetFqns
            .Select(fqn => ResolveSymbolId(snapshotId, fqn))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, routed);
    }

    [Fact]
    public async Task AspNetCoreAdapter_SilentHttpMethodAndSubclassedHttpGet_ProduceExpectedRoutes()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Controller.cs"] = """
                                    using Microsoft.AspNetCore.Mvc;

                                    namespace App;

                                    public sealed class CustomGetAttribute : HttpGetAttribute
                                    {
                                        public CustomGetAttribute(string template) : base(template) { }
                                    }

                                    [Route("api/[controller]")]
                                    public class MixedController : ControllerBase
                                    {
                                        [HttpGet]
                                        [HttpPost("x")]
                                        public int Mixed() => 1;

                                        [CustomGet("custom")]
                                        public int Custom() => 2;
                                    }
                                    """
            },
            frameworkReferences: [AspNetCoreFramework]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var mixedId = ResolveSymbolId(snapshotId, "global::App.MixedController.Mixed");
        var customId = ResolveSymbolId(snapshotId, "global::App.MixedController.Custom");
        var routes = QueryEdges(snapshotId, "RoutesTo", Provenance.FrameworkDerived)
            .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
            .OrderBy(e => e.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetSymbolId, StringComparer.Ordinal)
            .ToList();

        var expected = new (string Source, string Target)[]
            {
                ("route://GET /api/Mixed", mixedId),
                ("route://GET /api/Mixed/custom", customId),
                ("route://POST /api/Mixed/x", mixedId)
            }
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, routes);
    }

    [Fact]
    public async Task DependencyInjectionAdapter_AddScopedProducesRegisters()
    {
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
                                      """
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
        {
            Assert.Equal(new DependencyInjectionAdapter().Version, edge.ExtractorVersion);
            // Line 11: "        services.AddScoped<IService, Service>();" -> invocation spans col 8..47.
            Assert.Equal((11, 8, 11, 47),
                (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
        }
    }

    /// <summary>
    ///     D8, S02-F8: top-level statements are owned by the compiler-made entry point, which Roslyn
    ///     displays as <c>&lt;top-level-statements-entry-point&gt;</c> of the type <c>Program</c>; a
    ///     lambda in a top-level statement is owned by the same entry point.
    /// </summary>
    [Fact]
    public async Task DependencyInjectionAdapter_TopLevelStatementAddScopedProducesRegisters()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Service.cs"] = """
                                 namespace App;

                                 public interface IService { }
                                 public class Service : IService { }
                                 public interface IK { }
                                 public class K : IK { }
                                 """,
                ["Program.cs"] = """
                                 using System;
                                 using Microsoft.Extensions.DependencyInjection;
                                 using App;

                                 IServiceCollection services = null!;
                                 services.AddScoped<IService, Service>();
                                 Action<IServiceCollection> configure = s => s.AddScoped<IK, K>();
                                 configure(services);
                                 """
            },
            frameworkReferences: [AspNetCoreFramework],
            msbuildProperties: new Dictionary<string, string> { ["OutputType"] = "Exe" });

        var snapshotId = await RunFullIndexAsync(DbPath);

        var entryPointId = ResolveSymbolId(snapshotId, "global::Program.<top-level-statements-entry-point>");
        var serviceId = ResolveSymbolId(snapshotId, "global::App.Service");
        var kId = ResolveSymbolId(snapshotId, "global::App.K");
        var expected = new[]
        {
            (entryPointId, serviceId),
            (ResolveSymbolId(snapshotId, "global::App.IService"), serviceId),
            (entryPointId, kId),
            (ResolveSymbolId(snapshotId, "global::App.IK"), kId)
        }.Order().ToList();

        var registers = QueryEdges(snapshotId, "Registers", Provenance.FrameworkDerived);
        Assert.Equal(expected, registers.Select(e => (e.SourceSymbolId, e.TargetSymbolId)).Order().ToList());
        Assert.All(registers, edge => Assert.Equal(new DependencyInjectionAdapter().Version, edge.ExtractorVersion));
    }

    /// <summary>
    ///     D28: a scan outside the modeled chain (here <c>AsSelf</c>) gives one placeholder for the
    ///     scanned assembly, from the Scan call. The placeholder is a Convention graph node. The
    ///     project App has the SDK default identity (version 1.0.0.0, neutral culture, no key).
    /// </summary>
    [Fact]
    public async Task DIConventionMatcher_UnmodeledScanProducesConventionPlaceholder()
    {
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
                                                  .FromAssemblyOf<App>()
                                                  .AddClasses()
                                                  .AsSelf()
                                                  .WithScopedLifetime());
                                          }
                                      }
                                      """
            },
            ["Scrutor@4.2.2"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var registers = QueryEdges(snapshotId, "Registers", Provenance.Convention);
        var edge = Assert.Single(registers);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Registration.Configure"), edge.SourceSymbolId);
        Assert.Equal("convention:assembly_scan:App, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", edge.TargetSymbolId);
        Assert.Equal(new DependencyInjectionAdapter().Version, edge.ExtractorVersion);

        // Boundary membership is recorded in snapshot_graph_nodes (D153).
        await using (var connection = new SqliteConnection($"Data Source={DbPath}"))
        {
            connection.Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM snapshot_graph_nodes WHERE snapshot_id = @snapshotId AND node_id = @nodeId;";
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@nodeId", edge.TargetSymbolId);
            var actual = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1L, actual);
        }
    }

    [Fact]
    public async Task MediatRAdapter_RequestHandlerProducesHandles()
    {
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
                              """
            },
            ["MediatR@12.4.1"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var handles = QueryEdges(snapshotId, "Handles", Provenance.FrameworkDerived);
        var edge = Assert.Single(handles);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Ping"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.PingHandler.Handle"), edge.TargetSymbolId);
        Assert.Equal(new MediatRAdapter().Version, edge.ExtractorVersion);
        // Line 8: "    public Task<string> Handle(Ping request, CancellationToken ct) => Task.FromResult(\"pong\");" -> col 4..94.
        Assert.Equal((8, 4, 8, 94),
            (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
    }

    [Fact]
    public async Task MediatRAdapter_Handles_TargetsTheImplementationOfEachInterface()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Handlers.cs"] = """
                                  using System.Threading;
                                  using System.Threading.Tasks;
                                  using MediatR;

                                  namespace App;

                                  public record Ping : IRequest<string>;
                                  public record Pong : IRequest<string>;

                                  public class Multi : IRequestHandler<Ping, string>, IRequestHandler<Pong, string>
                                  {
                                      public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("ping");
                                      public Task<string> Handle(Pong request, CancellationToken cancellationToken) => Task.FromResult("pong");
                                  }

                                  public record Q : IRequest;

                                  public class ExplicitHandler : IRequestHandler<Q>
                                  {
                                      Task IRequestHandler<Q>.Handle(Q request, CancellationToken cancellationToken) => Task.CompletedTask;
                                  }

                                  public record N : INotification;

                                  public abstract class BaseN<T> : INotificationHandler<T> where T : INotification
                                  {
                                      public Task Handle(T notification, CancellationToken cancellationToken) => Task.CompletedTask;
                                  }

                                  public class NHandler : BaseN<N> { }
                                  """
            },
            ["MediatR@12.4.1"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var handles = QueryEdges(snapshotId, "Handles", Provenance.FrameworkDerived);
        var expectedVersion = new MediatRAdapter().Version;
        Assert.All(handles, h => Assert.Equal(expectedVersion, h.ExtractorVersion));

        string FindSymbolId(Func<IndexedSymbolInfo, string, bool> predicate)
        {
            using var store = OpenStore(DbPath);
            try
            {
                foreach (var id in store.GetSymbolIdsInSnapshot(snapshotId))
                {
                    var info = store.GetSymbolInfo(id, snapshotId);
                    if (info != null && predicate(info, id))
                        return id;
                }
            }
            finally
            {
                store.Close();
            }

            throw new InvalidOperationException("Matching symbol not found.");
        }

        var pingHandleId = FindSymbolId((info, id) => info.FullyQualifiedName == "global::App.Multi.Handle" && id.Contains("App.Ping", StringComparison.Ordinal));
        var pongHandleId = FindSymbolId((info, id) => info.FullyQualifiedName == "global::App.Multi.Handle" && id.Contains("App.Pong", StringComparison.Ordinal));
        var explicitHandleId = FindSymbolId((info, _) => info.FullyQualifiedName != null && info.FullyQualifiedName.StartsWith("global::App.ExplicitHandler.", StringComparison.Ordinal) && info.FullyQualifiedName.EndsWith(".Handle", StringComparison.Ordinal));
        var baseNHandleId = FindSymbolId((info, _) => info.FullyQualifiedName != null && info.FullyQualifiedName.StartsWith("global::App.BaseN", StringComparison.Ordinal) && info.FullyQualifiedName.EndsWith(".Handle", StringComparison.Ordinal));

        var actual = handles
            .Select(h => (h.SourceSymbolId, h.TargetSymbolId))
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToArray();

        var expected = new (string Source, string Target)[]
            {
                (ResolveSymbolId(snapshotId, "global::App.Ping"), pingHandleId),
                (ResolveSymbolId(snapshotId, "global::App.Pong"), pongHandleId),
                (ResolveSymbolId(snapshotId, "global::App.Q"), explicitHandleId),
                (ResolveSymbolId(snapshotId, "global::App.N"), baseNHandleId)
            }
            .OrderBy(p => p.Source, StringComparer.Ordinal)
            .ThenBy(p => p.Target, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);

        var nId = ResolveSymbolId(snapshotId, "global::App.N");
        var nEdge = Assert.Single(handles, h => h.SourceSymbolId == nId);
        Assert.EndsWith("Handlers.cs", nEdge.SourceDocumentPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EfCoreAdapter_DbContextDbSetProducesMapsTo()
    {
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
                                  """
            },
            ["Microsoft.EntityFrameworkCore@10.0.1"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var mapsTo = QueryEdges(snapshotId, "MapsTo", Provenance.FrameworkDerived);
        var edge = Assert.Single(mapsTo);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.AppDbContext.Blogs"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Blog"), edge.TargetSymbolId);
        Assert.Equal(new EfCoreAdapter().Version, edge.ExtractorVersion);
    }

    [Fact]
    public async Task EfCoreAdapter_InheritedGenericDbSetsMapOnlySolutionEntities()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["BaseContext.cs"] = """
                                     using Microsoft.EntityFrameworkCore;

                                     namespace App;

                                     public class BaseContext<T> : DbContext where T : class
                                     {
                                         public DbSet<T> Items { get; set; } = null!;
                                         public DbSet<System.Tuple<int>> ExternalItems { get; set; } = null!;
                                         private DbSet<HiddenWidget> PrivateItems { get; set; } = null!;
                                         public static DbSet<HiddenWidget> StaticItems { get; set; } = null!;
                                     }

                                     public class IntermediateContext<T> : BaseContext<T> where T : class;
                                     public class HiddenWidget;
                                     """,
                ["AppContext.cs"] = """
                                    namespace App;

                                    public class AppContext : IntermediateContext<Widget>;
                                    public class Widget;
                                    """
            },
            ["Microsoft.EntityFrameworkCore@10.0.1"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var contextId = ResolveSymbolId(snapshotId, "global::App.AppContext");
        var mapsTo = QueryEdges(snapshotId, "MapsTo", Provenance.FrameworkDerived);
        var edge = Assert.Single(mapsTo, e => e.SourceSymbolId == contextId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Widget"), edge.TargetSymbolId);
        Assert.EndsWith("AppContext.cs", edge.SourceDocumentPath, StringComparison.Ordinal);
        Assert.Equal(2, edge.SourceStartLine);
        Assert.DoesNotContain(mapsTo, e => e.TargetSymbolId.Contains("System.Tuple", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EfCoreAdapter_HasQueryFilterProducesQueryFilterAnnotation()
    {
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
                                  """
            },
            ["Microsoft.EntityFrameworkCore@10.0.1"]);

        RestoreSolution();
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
    public async Task EfCoreAdapter_ModelBuilderAndConfigurations_GiveTheSameFacts()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Data.cs"] = """
                              using System.Collections.Generic;
                              using Microsoft.EntityFrameworkCore;

                              namespace App;

                              public class Blog
                              {
                                  public int Id { get; set; }
                                  public string Url { get; set; } = "";
                                  public List<Post> Posts { get; set; } = new();
                              }

                              public class Post
                              {
                                  public int Id { get; set; }
                                  public string Title { get; set; } = "";
                                  public Blog Blog { get; set; } = null!;
                              }

                              public class Tag
                              {
                                  public int Id { get; set; }
                                  public bool Hidden { get; set; }
                              }

                              public class Author
                              {
                                  public int Id { get; set; }
                              }

                              public class AppDb : DbContext
                              {
                                  public DbSet<Blog> Blogs { get; set; } = null!;

                                  protected override void OnModelCreating(ModelBuilder modelBuilder)
                                  {
                                      modelBuilder.Entity<Post>().HasIndex(p => p.Title).HasDatabaseName("IX_Title");
                                      modelBuilder.Entity<Blog>().HasIndex(b => b.Url).IsUnique();
                                      modelBuilder.Entity<Post>().HasIndex(p => new { p.Title, p.Id }).IsUnique().HasDatabaseName("UX_Post");
                                      modelBuilder.Entity<Blog>().HasMany(b => b.Posts).WithOne(p => p.Blog);
                                  }
                              }
                              """,
                ["Config.cs"] = """
                                using Microsoft.EntityFrameworkCore;
                                using Microsoft.EntityFrameworkCore.Metadata.Builders;

                                namespace App;

                                public class TwoConfig : IEntityTypeConfiguration<Tag>, IEntityTypeConfiguration<Author>
                                {
                                    public void Configure(EntityTypeBuilder<Tag> b)
                                    {
                                        b.HasQueryFilter(t => !t.Hidden);
                                    }

                                    public void Configure(EntityTypeBuilder<Author> b)
                                    {
                                    }
                                }

                                public class BlogConfig : IEntityTypeConfiguration<Blog>
                                {
                                    public void Configure(EntityTypeBuilder<Blog> b)
                                    {
                                        b.HasMany(x => x.Posts).WithOne();
                                    }
                                }
                                """
            },
            ["Microsoft.EntityFrameworkCore.Relational@10.0.13"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var appDbId = ResolveSymbolId(snapshotId, "global::App.AppDb");
        var appDbBlogsId = ResolveSymbolId(snapshotId, "global::App.AppDb.Blogs");
        var blogId = ResolveSymbolId(snapshotId, "global::App.Blog");
        var postId = ResolveSymbolId(snapshotId, "global::App.Post");
        var tagId = ResolveSymbolId(snapshotId, "global::App.Tag");
        var authorId = ResolveSymbolId(snapshotId, "global::App.Author");
        var twoConfigId = ResolveSymbolId(snapshotId, "global::App.TwoConfig");
        var blogConfigId = ResolveSymbolId(snapshotId, "global::App.BlogConfig");

        var mapsTo = QueryEdges(snapshotId, "MapsTo", Provenance.FrameworkDerived)
            .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToList();
        var expectedMapsTo = new (string SourceSymbolId, string TargetSymbolId)[]
            {
                (appDbBlogsId, blogId),
                (appDbId, postId),
                (appDbId, blogId),
                (twoConfigId, tagId),
                (twoConfigId, authorId),
                (blogConfigId, blogId)
            }
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedMapsTo, mapsTo);

        var refs = QueryEdges(snapshotId, "References", Provenance.FrameworkDerived)
            .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToList();
        var expectedRefs = new (string SourceSymbolId, string TargetSymbolId)[]
            {
                (appDbId, postId),
                (blogConfigId, postId)
            }
            .OrderBy(p => p.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(p => p.TargetSymbolId, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedRefs, refs);

        using var store = OpenStore(DbPath);
        var annotations = store.GetAnnotations(snapshotId)
            .Select(a => (a.SymbolId, a.Kind, a.Value, File: Path.GetFileName(a.DocumentPath)))
            .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
            .ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Value, StringComparer.Ordinal)
            .ToList();
        var expectedAnnotations = new (string SymbolId, string Kind, string Value, string? File)[]
            {
                (blogId, "ef_unique_index_constraint", "Blog(Url)", "Data.cs"),
                (postId, "ef_unique_index_constraint", "Post(Title, Id) name=UX_Post", "Data.cs"),
                (tagId, "ef_query_filter_constraint", "Tag: HasQueryFilter: t => !t.Hidden", "Config.cs")
            }
            .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
            .ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Value, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expectedAnnotations, annotations);
    }

    [Fact]
    public async Task SerializationAdapter_RecordsTheWireContractOfEachMember()
    {
        var test = new SerializationAdapterExtractorTest();
        try
        {
            var extraction = await test.ExtractAsync(
                new Dictionary<string, string>
                {
                    ["Contract.cs"] = """
                                      using System.Collections.Generic;
                                      using System.Runtime.Serialization;
                                      using System.Text.Json.Serialization;

                                      namespace App;

                                      public class Inner { }

                                      public class Dto
                                      {
                                          [JsonPropertyName("a")]
                                          public Inner A { get; set; } = new();

                                          [System.Text.Json.Serialization.JsonPropertyName("b")]
                                          public Inner B { get; set; } = new();

                                          [JsonIgnore]
                                          public Inner C { get; set; } = new();

                                          public Inner D { get; set; } = new();

                                          [JsonInclude]
                                          internal int E;

                                          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
                                          public string? F { get; set; }

                                          [JsonIgnore]
                                          [JsonPropertyName("g")]
                                          public int G { get; set; }

                                          [JsonIgnore(Condition = JsonIgnoreCondition.Always)]
                                          public int H { get; set; }

                                          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
                                          public int I { get; set; }
                                      }

                                      public record RDto([property: JsonPropertyName("x")] int X);

                                      [DataContract]
                                      public class DcDto
                                      {
                                          [DataMember(Name = "w")]
                                          public int W { get; set; }

                                          [DataMember]
                                          public int V { get; set; }

                                          [IgnoreDataMember]
                                          public int U { get; set; }
                                      }

                                      public class Order { }

                                      [JsonSerializable(typeof(List<Order>))]
                                      internal partial class Ctx { }
                                      """
                },
                runAdapters: true);

            var annotations = extraction.Result.Annotations
                .Where(a => a.Kind == "serialization_contract")
                .Select(a => (a.SymbolId, a.Value))
                .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
                .ThenBy(a => a.Value, StringComparer.Ordinal)
                .ToList();

            var expectedAnnotations = new (string SymbolId, string Value)[]
                {
                    (extraction.ResolveId("global::App.Dto.A"), "name=a"),
                    (extraction.ResolveId("global::App.Dto.B"), "name=b"),
                    (extraction.ResolveId("global::App.Dto.C"), "ignored"),
                    (extraction.ResolveId("global::App.Dto.E"), "included"),
                    (extraction.ResolveId("global::App.Dto.F"), "included"),
                    (extraction.ResolveId("global::App.Dto.G"), "ignored"),
                    (extraction.ResolveId("global::App.Dto.H"), "ignored"),
                    (extraction.ResolveId("global::App.Dto.I"), "included"),
                    (extraction.ResolveId("global::App.RDto.X"), "name=x"),
                    (extraction.ResolveId("global::App.DcDto.W"), "name=w"),
                    (extraction.ResolveId("global::App.DcDto.V"), "included"),
                    (extraction.ResolveId("global::App.DcDto.U"), "ignored")
                }
                .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
                .ThenBy(a => a.Value, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(expectedAnnotations, annotations);

            var serializationVersion = new SerializationAdapter().Version;
            var adapterEdges = extraction.Result.Edges
                .Where(e => e.ExtractorVersion == serializationVersion)
                .ToList();
            Assert.Equal(2, adapterEdges.Count);
            Assert.All(adapterEdges, e =>
            {
                Assert.Equal("References", e.Kind);
                Assert.Equal(Provenance.FrameworkDerived, e.Provenance);
                Assert.Equal(extraction.ResolveId("global::App.Ctx"), e.SourceSymbolId);
            });
            Assert.Contains(adapterEdges, e => e.TargetSymbolId.StartsWith("T:System.Collections.Generic.List`1|", StringComparison.Ordinal));
            Assert.Contains(adapterEdges, e => e.TargetSymbolId == extraction.ResolveId("global::App.Order"));
        }
        finally
        {
            test.Dispose();
        }
    }

    [Fact]
    public async Task SerializationAdapter_IgnoresAttributesTheSerializerIgnores()
    {
        var test = new SerializationAdapterExtractorTest();
        try
        {
            var extraction = await test.ExtractAsync(
                new Dictionary<string, string>
                {
                    ["Contract.cs"] = """
                                      using System.Runtime.Serialization;

                                      namespace App;

                                      public class NoContract
                                      {
                                          [DataMember]
                                          internal int A { get; set; }

                                          [System.Text.Json.Serialization.JsonPropertyName("b")]
                                          internal int B { get; set; }

                                          [IgnoreDataMember]
                                          public int C { get; set; }
                                      }

                                      [DataContract]
                                      public class WithContract
                                      {
                                          [DataMember]
                                          internal int D { get; set; }
                                      }
                                      """
                },
                runAdapters: true);

            var annotations = extraction.Result.Annotations
                .Where(a => a.Kind == "serialization_contract")
                .Select(a => (a.SymbolId, a.Value))
                .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
                .ThenBy(a => a.Value, StringComparer.Ordinal)
                .ToList();

            var expected = new (string SymbolId, string Value)[]
                {
                    (extraction.ResolveId("global::App.NoContract.C"), "ignored"),
                    (extraction.ResolveId("global::App.WithContract.D"), "included")
                }
                .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
                .ThenBy(a => a.Value, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(expected, annotations);
        }
        finally
        {
            test.Dispose();
        }
    }

    [Fact]
    public async Task SerializationAdapter_ReadsNewtonsoftAttributes()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["N1.cs"] = """
                            using Newtonsoft.Json;

                            namespace App;

                            public class N1
                            {
                                [JsonProperty("n")]
                                public int A { get; set; }

                                [JsonProperty]
                                private int B { get; set; }

                                [Newtonsoft.Json.JsonIgnore]
                                public int C { get; set; }
                            }
                            """
            },
            ["Newtonsoft.Json@13.0.3"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        var annotations = store.GetAnnotations(snapshotId)
            .Where(a => a.Kind == "serialization_contract")
            .Select(a => (a.SymbolId, a.Value))
            .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
            .ThenBy(a => a.Value, StringComparer.Ordinal)
            .ToList();

        var expected = new (string SymbolId, string Value)[]
            {
                (ResolveSymbolId(snapshotId, "global::App.N1.A"), "name=n"),
                (ResolveSymbolId(snapshotId, "global::App.N1.B"), "included"),
                (ResolveSymbolId(snapshotId, "global::App.N1.C"), "ignored")
            }
            .OrderBy(a => a.SymbolId, StringComparer.Ordinal)
            .ThenBy(a => a.Value, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, annotations);
    }

    [Fact]
    public async Task TestAdapter_TestProjectProducesTestedBy()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Calculator.cs"] = """
                                    namespace App;

                                    public class Calculator
                                    {
                                        public int Add(int a, int b) => a + b;
                                    }
                                    """
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
                                         """
            },
            projectReferences: ["App"],
            packageReferences: ["xunit@2.9.3"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived);
        var edge = Assert.Single(testedBy);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.Calculator"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::CalculatorTests.Add"), edge.TargetSymbolId);
        Assert.Equal(new TestAdapter().Version, edge.ExtractorVersion);
        // Line 4: "    [Fact]" (col 4) through line 8: "    }" (col 5).
        Assert.Equal((4, 4, 8, 5),
            (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
    }

    /// <summary>
    ///     Regression: namespace segments of a fully qualified name bind as member
    ///     accesses and resolve to <see cref="Microsoft.CodeAnalysis.INamespaceSymbol" />.
    ///     Namespaces are not snapshot symbols and the same-assembly guard does not stop
    ///     them, so before the fix they produced a TestedBy edge with an <c>N:</c> source
    ///     that the orphan filter silently dropped afterwards. Pattern B: the external
    ///     production endpoint never survives to a persisted snapshot, so the adapter
    ///     contract is pinned at extraction level.
    /// </summary>
    [Fact]
    public async Task TestAdapter_FullyQualifiedNameProducesNoNamespaceTestedBy()
    {
        var test = new TestAdapterExtractorTest();
        try
        {
            var extraction = await test.ExtractAsync(
                new Dictionary<string, string>
                {
                    ["FullyQualifiedTests.cs"] = """
                                                 using Xunit;

                                                 public class FullyQualifiedTests
                                                 {
                                                     [Fact]
                                                     public void UsesFullyQualifiedName()
                                                     {
                                                         System.Linq.Enumerable.Range(0, 1);
                                                     }
                                                 }
                                                 """
                },
                projectName: "App.Tests",
                runAdapters: true);

            var testedBy = extraction.EdgesOf("TestedBy", Provenance.FrameworkDerived);

            // Non-vacuous: the external production type still produces an edge.
            Assert.Contains(testedBy, e => e.SourceSymbolId.StartsWith("T:System.Linq.Enumerable", StringComparison.Ordinal));

            // Namespace segments must not become TestedBy sources.
            Assert.DoesNotContain(testedBy, e => e.SourceSymbolId.StartsWith("N:", StringComparison.Ordinal));
        }
        finally
        {
            test.Dispose();
        }
    }

    // ── R5 characterization tests ──────────────────────────────────────

    /// <summary>
    ///     Characterization (R5): MediatR adapter only recognizes
    ///     <c>IRequestHandler</c> and <c>INotificationHandler</c>. Other handler
    ///     patterns (stream handlers) are silently skipped — no edge is emitted,
    ///     and no false "proved" claim is made.
    /// </summary>
    [Fact]
    public async Task MediatRAdapter_StreamHandler_IsSilentlySkipped()
    {
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
                                    """
            },
            ["MediatR@12.4.1"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        // Stream handler pattern is not recognized — zero Handles edges.
        var handles = QueryEdges(snapshotId, "Handles", Provenance.FrameworkDerived);
        Assert.Empty(handles);

        // Confirm no false Registers claim either.
        var registers = QueryEdges(snapshotId, "Registers", Provenance.FrameworkDerived);
        Assert.Empty(registers);

        // After the fix: stream handler emits an "unmodeled_mediatr_pattern" annotation.
        using var store = OpenStore(DbPath);
        var annotations = store.GetAnnotations(snapshotId);
        Assert.Contains(annotations, a =>
            a is { Kind: "unmodeled_mediatr_pattern", Value: "IStreamRequestHandler" });
    }

    /// <summary>
    ///     Characterization (R5): <see cref="DeclaredBoundaries.Known" /> registry
    ///     contains exactly the expected entries. This is a lightweight staleness
    ///     check — if a new boundary is added or removed, this test forces a
    ///     conscious update.
    /// </summary>
    [Fact]
    public void DeclaredBoundaries_RegistryContainsExpectedEntries()
    {
        var expectedIds = new[]
        {
            "di_external_extension",
            "di_hosted_service",
            "di_options",
            "ef_convention",
            "intermediate_redirect_not_applied",
            "masstransit_consumer",
            "mediatr_exception_handler",
            "mediatr_pipeline_behavior",
            "mediatr_pre_post_processor",
            "mediatr_stream_handler",
            "minimal_api_endpoints",
            "multi_target_union",
            "non_csharp_projects",
            "razor_blazor",
            "shape_similarity",
            "source_generators",
            "static_abstract_dispatch"
        };

        var actualIds = DeclaredBoundaries.Known.Select(e => e.Id).OrderBy(id => id).ToList();
        var expected = expectedIds.OrderBy(id => id).ToList();

        Assert.Equal(expected, actualIds);
    }

    [Fact]
    public async Task TestAdapter_DerivedTestAttributes_MarkTestMethods()
    {
        var test = new TestAdapterExtractorTest();
        try
        {
            var extraction = await test.ExtractAsync(
                new Dictionary<string, string>
                {
                    ["Tests.cs"] = """
                                   using System;

                                   namespace App.Tests;

                                   public sealed class MyFactAttribute : Xunit.FactAttribute { }
                                   public sealed class TestAttribute : Attribute { }

                                   public class T
                                   {
                                       [Xunit.SkippableFact]
                                       public void Skippable() { new System.Text.StringBuilder(); }

                                       [MyFact]
                                       public void Mine() { new System.Text.StringBuilder(); }

                                       [Test]
                                       public void NotATest() { new System.Text.StringBuilder(); }
                                   }
                                   """
                },
                projectName: "App.Tests",
                runAdapters: true);

            var testedBy = extraction.EdgesOf("TestedBy", Provenance.FrameworkDerived)
                .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
                .OrderBy(e => e.TargetSymbolId, StringComparer.Ordinal)
                .ToList();

            var expected = new (string Source, string Target)[]
                {
                    ("T:System.Text.StringBuilder", extraction.ResolveId("global::App.Tests.T.Skippable")),
                    ("T:System.Text.StringBuilder", extraction.ResolveId("global::App.Tests.T.Mine"))
                }
                .OrderBy(e => e.Target, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(expected.Count, testedBy.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.StartsWith(expected[i].Source + "|", testedBy[i].SourceSymbolId, StringComparison.Ordinal);
                Assert.Equal(expected[i].Target, testedBy[i].TargetSymbolId);
            }
        }
        finally
        {
            test.Dispose();
        }
    }

    [Fact]
    public async Task TestAdapter_NUnitAndMSTestAttributes_MarkTestMethods()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                            namespace App;

                            public class E { }
                            public class F { }
                            public class C { }
                            public class T5 { }
                            """
        });

        CreateProject("App.NUnit", new Dictionary<string, string>
        {
            ["NUnitTests.cs"] = """
                                using NUnit.Framework;

                                namespace App.NUnit;

                                public class NUnitTests
                                {
                                    public static int[] Src = { 1 };

                                    [TestCase(1)]
                                    public void Case(int x) { new App.E(); }

                                    [TestCaseSource(nameof(Src))]
                                    public void Source(int x) { new App.F(); }
                                }
                                """
        },
        packageReferences: ["NUnit@5.0.0"],
        projectReferences: ["App"]);

        CreateProject("App.MsTest", new Dictionary<string, string>
        {
            ["MsTests.cs"] = """
                             using Microsoft.VisualStudio.TestTools.UnitTesting;

                             namespace App.MsTest;

                             [TestClass]
                             public class MsTests
                             {
                                 [DataTestMethod]
                                 [DataRow(1)]
                                 public void Data(int x) { new App.C(); }

                                 [TestMethod]
                                 public void Plain() { new App.T5(); }
                             }
                             """
        },
        packageReferences: ["MSTest.TestFramework@4.5.2"],
        projectReferences: ["App"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived)
            .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
            .OrderBy(e => e.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetSymbolId, StringComparer.Ordinal)
            .ToList();

        var expected = new (string Source, string Target)[]
            {
                (ResolveSymbolId(snapshotId, "global::App.E"), ResolveSymbolId(snapshotId, "global::App.NUnit.NUnitTests.Case")),
                (ResolveSymbolId(snapshotId, "global::App.F"), ResolveSymbolId(snapshotId, "global::App.NUnit.NUnitTests.Source")),
                (ResolveSymbolId(snapshotId, "global::App.C"), ResolveSymbolId(snapshotId, "global::App.MsTest.MsTests.Data")),
                (ResolveSymbolId(snapshotId, "global::App.T5"), ResolveSymbolId(snapshotId, "global::App.MsTest.MsTests.Plain"))
            }
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, testedBy);
    }

    [Fact]
    public async Task TestAdapter_ProjectNamedTests_WithoutAFramework_IsNotATestProject()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["T1.cs"] = """
                        namespace App;

                        public class T1 { }
                        """
        });

        CreateProject("Foo.Tests", new Dictionary<string, string>
        {
            ["Checks.cs"] = """
                            using System;

                            namespace Foo.Tests;

                            public sealed class TestAttribute : Attribute { }

                            public class Checks
                            {
                                [Test]
                                public void Run() { new App.T1(); }
                            }
                            """
        },
        projectReferences: ["App"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived);
        Assert.Empty(testedBy);
    }

    [Fact]
    public async Task TestAdapter_TUnitProject_IsATestProject()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["T6.cs"] = """
                        namespace App;

                        public class T6 { }
                        """
        });

        CreateProject("Checks", new Dictionary<string, string>
        {
            ["C.cs"] = """
                       namespace Checks;

                       public class C
                       {
                           [TUnit.Core.Test]
                           public void Run() { new App.T6(); }
                       }
                       """
        },
        packageReferences: ["TUnit.Core@1.78.0"],
        projectReferences: ["App"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived);
        var edge = Assert.Single(testedBy);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::App.T6"), edge.SourceSymbolId);
        Assert.Equal(ResolveSymbolId(snapshotId, "global::Checks.C.Run"), edge.TargetSymbolId);
    }

    [Fact]
    public async Task TestAdapter_TestedBy_FollowsHelpersSetupAndTargetTypedNew()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Models.cs"] = """
                            namespace App;

                            public class A { }
                            public class B { }
                            public class C { }
                            public class D { }
                            public class E { }
                            public class F { }
                            public class G { }
                            public class H { }
                            public class Svc { public static void Run() { } }
                            """
        });

        CreateProject("App.Tests", new Dictionary<string, string>
        {
            ["Fixture.cs"] = """
                             namespace App.Tests;

                             public static class Fixture
                             {
                                 public static object MakeG() => new App.G();
                             }

                             public class Holder
                             {
                                 public App.H Prop => new App.H();
                             }
                             """,
            ["CoverageTests.cs"] = """
                                   using Xunit;

                                   namespace App.Tests;

                                   public class CoverageTests
                                   {
                                       private readonly App.C _c = new App.C();

                                       public CoverageTests()
                                       {
                                           new App.D();
                                       }

                                       [Fact]
                                       public void TargetNew()
                                       {
                                           App.F f = new();
                                       }

                                       [Fact]
                                       public void ViaHelper()
                                       {
                                           Fixture.MakeG();
                                       }

                                       [Fact]
                                       public void ViaProperty()
                                       {
                                           _ = new Holder().Prop;
                                       }

                                       [Fact]
                                       public void ViaLocal()
                                       {
                                           Local();
                                           static void Local() { App.Svc.Run(); }
                                       }
                                   }
                                   """
        },
        packageReferences: ["xunit@2.9.3"],
        projectReferences: ["App"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var cId = ResolveSymbolId(snapshotId, "global::App.C");
        var dId = ResolveSymbolId(snapshotId, "global::App.D");
        var fId = ResolveSymbolId(snapshotId, "global::App.F");
        var gId = ResolveSymbolId(snapshotId, "global::App.G");
        var hId = ResolveSymbolId(snapshotId, "global::App.H");
        var svcId = ResolveSymbolId(snapshotId, "global::App.Svc");

        var targetNewId = ResolveSymbolId(snapshotId, "global::App.Tests.CoverageTests.TargetNew");
        var viaHelperId = ResolveSymbolId(snapshotId, "global::App.Tests.CoverageTests.ViaHelper");
        var viaPropertyId = ResolveSymbolId(snapshotId, "global::App.Tests.CoverageTests.ViaProperty");
        var viaLocalId = ResolveSymbolId(snapshotId, "global::App.Tests.CoverageTests.ViaLocal");

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived)
            .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
            .OrderBy(e => e.SourceSymbolId, StringComparer.Ordinal)
            .ThenBy(e => e.TargetSymbolId, StringComparer.Ordinal)
            .ToList();

        var expected = new (string Source, string Target)[]
            {
                (cId, targetNewId),
                (dId, targetNewId),
                (fId, targetNewId),
                (cId, viaHelperId),
                (dId, viaHelperId),
                (gId, viaHelperId),
                (cId, viaPropertyId),
                (dId, viaPropertyId),
                (hId, viaPropertyId),
                (cId, viaLocalId),
                (dId, viaLocalId),
                (svcId, viaLocalId)
            }
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, testedBy);
    }

    [Fact]
    public async Task TestAdapter_UncalledTestPropertyGetter_DoesNotProduceTestedBy()
    {
        CreateProject("App",
            new Dictionary<string, string>
            {
                ["Prod.cs"] = """
                              namespace App;
                              public class UnusedFromTestProp { }
                              public class UnusedFromHelperProp { }
                              public class UsedFromInit { }
                              public class UsedFromRead { }
                              """
            });

        CreateProject("App.Tests",
            new Dictionary<string, string>
            {
                ["Tests.cs"] = """
                               using Xunit;
                               using App;

                               namespace App.Tests;

                               public class Helper
                               {
                                   public UnusedFromHelperProp HelperProp => new UnusedFromHelperProp();
                                   public UsedFromInit InitProp { get; } = new UsedFromInit();
                               }

                               public class PropertyTests
                               {
                                   public UnusedFromTestProp UnusedProp => new UnusedFromTestProp();
                                   public UsedFromRead ReadProp => new UsedFromRead();

                                   [Fact]
                                   public void Run()
                                   {
                                       _ = new Helper();
                                       _ = ReadProp;
                                   }
                               }
                               """
            },
            projectReferences: ["App"],
            packageReferences: ["xunit@2.9.3"]);

        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var usedInitId = ResolveSymbolId(snapshotId, "global::App.UsedFromInit");
        var usedReadId = ResolveSymbolId(snapshotId, "global::App.UsedFromRead");
        var testId = ResolveSymbolId(snapshotId, "global::App.Tests.PropertyTests.Run");

        var testedBy = QueryEdges(snapshotId, "TestedBy", Provenance.FrameworkDerived)
            .Select(e => (e.SourceSymbolId, e.TargetSymbolId))
            .OrderBy(e => e.SourceSymbolId, StringComparer.Ordinal)
            .ToList();

        var expected = new (string Source, string Target)[]
            {
                (usedInitId, testId),
                (usedReadId, testId)
            }
            .OrderBy(e => e.Source, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, testedBy);
    }
}

/// <summary>Pattern B host for the Serialization adapter golden test.</summary>
public sealed class SerializationAdapterExtractorTest : InMemoryTestBase
{
}

/// <summary>Pattern B host for the Test adapter regression test.</summary>
public sealed class TestAdapterExtractorTest : InMemoryTestBase
{
}