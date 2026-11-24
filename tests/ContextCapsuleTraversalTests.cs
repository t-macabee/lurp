using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class ContextCapsuleTraversalTests : IntegrationTestBase
{
    [Fact]
    public async Task Accessors_PreserveRoleSpecificImpactAndCapsuleWitnesses()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Members.cs"] = """
                namespace App;
                public class Members
                {
                    public int Value { get => OnGet(); set => OnSet(); }
                    public int InitOnly { init => OnInit(); }
                    public int Auto { get; set; }
                    public event System.Action Changed { add => OnAdd(); remove => OnRemove(); }
                    public int Read() => Value;
                    public int ReadAuto() => Auto;
                    public void Write() => Value = 1;
                    public void Increment() => Value++;
                    public void WriteLater() => Write();
                    public int ReadAndWriteLater() { WriteLater(); return Read(); }
                    public static Members Initialize() => new Members { InitOnly = 1 };
                    public void Subscribe(System.Action action) => Changed += action;
                    public void Unsubscribe(System.Action action) => Changed -= action;
                    private static int OnGet() => 1;
                    private static void OnSet() { }
                    private static void OnInit() { }
                    private static void OnAdd() { }
                    private static void OnRemove() { }
                }
                public interface ICounter { int Count { get; } }
                public class Counter : ICounter
                {
                    public int Count => 1;
                    public static int Read(ICounter counter) => counter.Count;
                }
                """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var reachability = new ImpactReachability(store, snapshot);
        var upstreamKinds = ImpactRequest.Parse("anchor", "upstream", null, null, null, null, null).Request!.AllowedKinds;
        var downstreamKinds = ImpactRequest.Parse("anchor", "downstream", null, null, null, null, null).Request!.AllowedKinds;

        CheckAccessor("Value", "Read", "OnGet", EdgeKind.Reads, "OnSet");
        CheckAccessor("Value", "Write", "OnSet", EdgeKind.Writes, "OnGet");
        CheckAccessor("InitOnly", "Initialize", "OnInit", EdgeKind.Writes);
        CheckAccessor("Changed", "Subscribe", "OnAdd", EdgeKind.Writes);
        CheckAccessor("Changed", "Unsubscribe", "OnRemove", EdgeKind.Writes);
        Assert.DoesNotContain(Capsule(Id("ReadAuto")).DirectCallees, item => item.SymbolId == Id("Auto"));

        var combined = reachability.Trace(Id("ReadAndWriteLater"), ImpactDirection.Downstream, 6, downstreamKinds);
        Assert.Equal([nameof(EdgeKind.Calls), nameof(EdgeKind.Reads), nameof(EdgeKind.Calls)],
            combined.WitnessPath(Id("OnGet")).Select(hop => hop.EdgeKind));
        Assert.Equal([nameof(EdgeKind.Calls), nameof(EdgeKind.Calls), nameof(EdgeKind.Writes), nameof(EdgeKind.Calls)],
            combined.WitnessPath(Id("OnSet")).Select(hop => hop.EdgeKind));

        var increment = reachability.Trace(Id("Increment"), ImpactDirection.Downstream, 2, downstreamKinds);
        Assert.Equal(1, Assert.Single(increment.Symbols, item => item.SymbolId == Id("Value")).ShortestPathCount);
        Assert.Equal(nameof(EdgeKind.Reads), increment.WitnessPath(Id("OnGet"))[0].EdgeKind);
        Assert.Equal(nameof(EdgeKind.Writes), increment.WitnessPath(Id("OnSet"))[0].EdgeKind);

        string CounterId(string name) => ResolveSymbolId(snapshot, $"global::App.{name}");

        var dispatch = Capsule(CounterId("Counter.Read")).DirectCallees;
        Assert.Contains(dispatch, item => item.SymbolId == CounterId("ICounter.Count") && item.Direct == true);
        Assert.Contains(dispatch, item => item.SymbolId == CounterId("Counter.Count") &&
            item.Relationship == CapsuleRelationship.IndirectDispatchCandidate && item.Direct == false);

        string Id(string name) => ResolveSymbolId(snapshot, $"global::App.Members.{name}");

        ContextCapsule Capsule(string anchor) => ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshot, anchor, null, null), new ContextAssemblyOptions(ContextIntent.Inspect, 100_000, 4), store, store);

        void CheckAccessor(string member, string caller, string body, EdgeKind access, string? otherBody = null)
        {
            var memberId = Id(member);
            var callerId = Id(caller);
            var bodyId = Id(body);
            var upstream = reachability.Trace(bodyId, ImpactDirection.Upstream, 4, upstreamKinds);
            Assert.Equal([nameof(EdgeKind.Calls), access.ToString()], upstream.WitnessPath(callerId).Select(hop => hop.EdgeKind));
            Assert.Contains(Capsule(bodyId).IncomingPaths, path => path.Hops.Count >= 2 &&
                path.Hops[0].TargetSymbolId == bodyId && path.Hops[1].SourceSymbolId == callerId);

            var downstream = reachability.Trace(callerId, ImpactDirection.Downstream, 4, downstreamKinds);
            Assert.Equal([access.ToString(), nameof(EdgeKind.Calls)], downstream.WitnessPath(bodyId).Select(hop => hop.EdgeKind));
            if (otherBody != null)
                Assert.DoesNotContain(downstream.Symbols, item => item.SymbolId == Id(otherBody));

            var memberTrace = reachability.Trace(memberId, ImpactDirection.Downstream, 1, downstreamKinds);
            Assert.Equal(bodyId, Assert.Single(memberTrace.WitnessPath(bodyId)).TargetSymbolId);
            var memberCapsule = Capsule(memberId);
            Assert.Contains(memberCapsule.DirectCallees, item => item.SymbolId == bodyId);
            Assert.Contains(memberCapsule.OutgoingPaths, path => path.Hops[0].TargetSymbolId == bodyId);
            Assert.Contains(Capsule(callerId).DirectCallees, item => item.SymbolId == memberId && item.EdgeKind == access.ToString());
        }
    }

    [Fact]
    public async Task DefaultDownstream_SeedsTypeMembersAndExcludesOwnershipAndTests()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Members.cs"] = """
                namespace App;
                public class Container
                {
                    public void Member() => Helper.Run();
                    public class Nested { }
                }
                public class Helper { public static void Run() { } }
                """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var anchor = ResolveSymbolId(snapshot, "global::App.Container");
        var body = ResolveSymbolId(snapshot, "global::App.Helper.Run");
        var member = ResolveSymbolId(snapshot, "global::App.Container.Member");
        var request = ImpactRequest.Parse(anchor, "downstream", null, null, null, null, null).Request!;

        Assert.NotNull(request.AllowedKinds);
        Assert.DoesNotContain(nameof(EdgeKind.Declares), request.AllowedKinds);
        Assert.DoesNotContain(nameof(EdgeKind.Contains), request.AllowedKinds);
        Assert.DoesNotContain(nameof(EdgeKind.TestedBy), request.AllowedKinds);
        var reachability = new ImpactReachability(store, snapshot);
        var filtered = reachability.Trace(anchor, ImpactDirection.Downstream, 1, request.AllowedKinds);
        Assert.Equal(member, Assert.Single(filtered.WitnessPath(body)).SourceSymbolId);
        Assert.DoesNotContain(filtered.Symbols, item => item.SymbolId == member);
        Assert.Contains(reachability.Trace(anchor, ImpactDirection.Downstream, 1).Symbols, item => item.SymbolId == member);
    }

    [Fact]
    public async Task PropertyAndEventAnchors_SeedAccessorBodiesWithAnyKindFilter()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Members.cs"] = """
                namespace App;
                public class Members
                {
                    public int Value { get => OnGet(); set => OnSet(); }
                    public event System.Action Changed { add => OnAdd(); remove => OnRemove(); }
                    private static int OnGet() => 1;
                    private static void OnSet() { }
                    private static void OnAdd() { }
                    private static void OnRemove() { }
                }
                """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var reachability = new ImpactReachability(store, snapshot);
        IReadOnlySet<string>?[] filters =
        [
            null,
            new HashSet<string>(StringComparer.Ordinal) { nameof(EdgeKind.Declares), nameof(EdgeKind.Calls) }
        ];

        foreach (var allowedEdgeKinds in filters)
        {
            var value = reachability.Trace(Id("Value"), ImpactDirection.Downstream, 2, allowedEdgeKinds);
            Assert.Equal([Id("OnGet"), Id("OnSet")],
                value.Symbols.Select(item => item.SymbolId).OrderBy(id => id, StringComparer.Ordinal));

            var changed = reachability.Trace(Id("Changed"), ImpactDirection.Downstream, 2, allowedEdgeKinds);
            Assert.Equal([Id("OnAdd"), Id("OnRemove")],
                changed.Symbols.Select(item => item.SymbolId).OrderBy(id => id, StringComparer.Ordinal));
        }

        string Id(string name) => ResolveSymbolId(snapshot, $"global::App.Members.{name}");
    }

    [Fact]
    public async Task DualAccessFrontier_IsTrueWhenEitherAccessorLeavesTheWitnessPath()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Flow.cs"] = """
                namespace App;
                public class Flow
                {
                    public void Start() { DoRead(); DoWrite(); }
                    public int DoRead() => Prop;
                    public void DoWrite() => Prop = 1;
                    public int Prop { get => 1; set => OnSet(); }
                    private static void OnSet() { }
                }
                """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var reachability = new ImpactReachability(store, snapshot);

        var result = reachability.Trace(Id("Start"), ImpactDirection.Downstream, maxDepth: 2);

        var prop = Assert.Single(result.Symbols, item => item.SymbolId == Id("Prop"));
        Assert.Equal(2, prop.Depth);
        Assert.True(prop.IsFrontier);
        Assert.Contains(result.WitnessLeafPaths(), path =>
            path.Truncated && path.Hops[^1].TargetSymbolId == Id("Prop"));

        string Id(string name) => ResolveSymbolId(snapshot, $"global::App.Flow.{name}");
    }

    [Fact]
    public async Task Constructs_ReportsSynthesizedConstructorAndAttributeCallers()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["Constructors.cs"] = """
                namespace App;
                public class Marker { }
                public class Explicit { public Explicit() { } }
                public class MarkerAttribute : System.Attribute { }
                [Marker]
                public class Host
                {
                    public Marker Build() => new();
                    public Explicit BuildExplicit() => new();
                }
                """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        Assert.Contains(Callers("Marker"), item => item.SymbolId == Id("Host.Build") && item.EdgeKind == nameof(EdgeKind.Constructs));
        Assert.Contains(Callers("MarkerAttribute"), item => item.SymbolId == Id("Host") && item.EdgeKind == nameof(EdgeKind.Constructs));
        Assert.Single(Callers("Explicit"), item => item.SymbolId == Id("Host.BuildExplicit"));

        string Id(string name) => ResolveSymbolId(snapshot, $"global::App.{name}");

        List<CapsuleItem> Callers(string name) => ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshot, Id(name), null, null), new ContextAssemblyOptions(ContextIntent.Inspect, 100_000, 2), store, store)
            .DirectCallers;
    }

    [Fact]
    public async Task Routes_AppearOnceAtTheShallowestCallerDepth()
    {
        CreateProject("App", new Dictionary<string, string>
        {
            ["DemoController.cs"] = """
                using Microsoft.AspNetCore.Mvc;
                namespace App;
                public class DemoController : ControllerBase
                {
                    [HttpGet("demo")]
                    public int Direct() => Target();

                    [HttpGet("demo")]
                    public int Indirect() => Direct();

                    private static int Target() => 1;
                }
                """
        }, frameworkReferences: ["Microsoft.AspNetCore.App"]);
        RestoreSolution();
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        const string routeId = "route://GET /demo";
        var direct = Capsule("Direct", 3);
        var route = Assert.Single(direct.DirectCallers, item => item.SymbolId == routeId);
        Assert.Equal("Route", route.Kind);
        Assert.Equal(routeId, route.FullyQualifiedName);
        Assert.Null(route.Source);
        Assert.Null(route.DocumentPath);
        Assert.DoesNotContain(direct.SecondDegreeContext, item => item.SymbolId == routeId);
        Assert.DoesNotContain(Capsule("Target", 1).SecondDegreeContext, item => item.SymbolId == routeId);
        Assert.Single(Capsule("Target", 3).SecondDegreeContext, item => item.SymbolId == routeId);

        ContextCapsule Capsule(string name, int maxHops) => ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshot, ResolveSymbolId(snapshot, $"global::App.DemoController.{name}"), null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, 100_000, maxHops), store, store);
    }
}
