using Lurp.Storage;
using Lurp.Shared;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     REGRESSION test for the context tiers' handling of <c>MethodGroupRef</c>
///     edges: a method used only as a method group (<c>xs.Select(F)</c>, event
///     <c>+= F</c>) has no <c>Calls</c> edge, so the direct_callers and
///     relevant_tests tiers must traverse <c>MethodGroupRef</c> as a caller
///     relation. Before the fix both tiers ignored the edge kind and the capsule
///     for F reported no caller.
/// </summary>
public sealed class MethodGroupRefContextTierTests : IntegrationTestBase
{
    private const string ProductionSource = """
                                            namespace TestProject;

                                            public delegate int ChangedHandler(int value);

                                            public sealed class EventSource
                                            {
                                                public event ChangedHandler? Changed;
                                            }

                                            public static class MethodGroupTargets
                                            {
                                                public static int F(int value) => value;

                                                public static int Run(EventSource source, int[] values)
                                                {
                                                    source.Changed += F;
                                                    return values.Select(F).Sum();
                                                }
                                            }
                                            """;

    private const string TestSource = """
                                      using Xunit;

                                      namespace TestProject.Tests;

                                      public sealed class MethodGroupTargetTests
                                      {
                                          [Fact]
                                          public void Run_IsCallable()
                                          {
                                              var source = new TestProject.EventSource();
                                              Assert.Equal(6, TestProject.MethodGroupTargets.Run(source, new[] { 1, 2, 3 }));
                                          }
                                      }
                                      """;

    [Fact]
    public async Task Capsule_FieldUse_PartitionsCallersAndOrdersTests()
    {
        CreateProject("Uses", new Dictionary<string, string>
        {
            ["Uses.cs"] = """
                          namespace Uses;
                          public static class State { public static int F; }
                          public static class Writer { public static void W() => State.F = 1; }
                          public static class Reader { public static int R() => State.F == 0 ? R() : State.F; }
                          public static class Outer
                          {
                              public static int A() => Reader.R();
                              public static int Z() => A();
                          }
                          public static class Coverage
                          {
                              public static void Z() { }
                              public static void Y() { }
                              public static void X() { }
                          }
                          """
        });
        var snapshotId = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);

        string Id(string name) => ResolveSymbolId(snapshotId, $"global::Uses.{name}");
        var field = Id("State.F");
        var reader = Id("Reader.R");
        var writer = Id("Writer.W");
        var outer = Id("Outer.A");
        var tests = new[] { Id("Coverage.Z"), Id("Coverage.Y"), Id("Coverage.X") };
        foreach (var (covered, test) in new[] { field, reader, outer }.Zip(tests).Reverse())
            store.SaveEdges(snapshotId, [new EdgeRecord
            {
                SourceSymbolId = covered,
                TargetSymbolId = test,
                Kind = nameof(EdgeKind.TestedBy),
                Provenance = Provenance.CompilerProved,
                ExtractorVersion = VersionConstants.ExtractorVersion
            }]);

        var capsule = ContextAssembler.ResolveAndAssemble(store, store, new ContextLookup(snapshotId, field, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, 50_000, 2), store, store);
        Assert.Equal([(reader, "Reads"), (writer, "Writes")],
            capsule.DirectCallers.Select(item => (item.SymbolId, item.EdgeKind)));
        Assert.Equal((outer, "Calls"), (Assert.Single(capsule.SecondDegreeContext).SymbolId, capsule.SecondDegreeContext[0].EdgeKind));
        Assert.Equal(tests, capsule.RelevantTests.Select(item => item.SymbolId));

        CheckPages("direct_callers", [reader, writer]);
        CheckPages("second_degree_context", [outer]);
        CheckPages("relevant_tests", tests);

        void CheckPages(string tier, string[] expected)
        {
            for (var offset = 0; offset < expected.Length; offset++)
            {
                var page = ContextAssembler.BuildTierPage(store, store, snapshotId, SymbolId.Parse(field),
                    tier, 2, false, offset, 1);
                Assert.Equal(expected[offset], Assert.Single(page.Items).SymbolId);
                Assert.Equal(offset, page.Offset);
                Assert.Equal(offset + 1 < expected.Length, page.HasMore);
            }
        }
    }

    [Fact]
    public async Task Capsule_DispatchAndAccessorRoutes_CountAllHops()
    {
        CreateProject("Uses", new Dictionary<string, string>
        {
            ["Uses.cs"] = """
                          namespace Uses;
                          public interface IState
                          {
                              int Get();
                              int Count { get; }
                              event System.Action Changed;
                          }
                          public class State : IState
                          {
                              public int F;
                              public int Count => F;
                              public int Value { get => F; set => F = value; }
                              public int Get() => F;
                              public event System.Action? Changed;
                              public void Raise() => Changed?.Invoke();
                          }
                          public class Base { public virtual int Get() => 0; }
                          public class Derived : Base
                          {
                              public override int Get() => 1;
                              public int BaseCall() => base.Get();
                          }
                          public static class Users
                          {
                              public static int B(State s) => s.Value;
                              public static void C(State s) => s.Value = 1;
                              public static int Read(IState s) => s.Count;
                              public static void DirectSubscribe(State s, System.Action handler) => s.Changed += handler;
                              public static void Subscribe(IState s, System.Action handler) => s.Changed += handler;
                              public static int Call(IState s) => s.Get();
                              public static int Both(State s) => s.Get() + ((IState)s).Get();
                              public static System.Func<IState, int> Group() => Call;
                              public static System.Func<int> GroupOfDispatch(IState s) => s.Get;
                          }
                          public static class Coverage
                          {
                              public static void Call() { }
                              public static void Read() { }
                              public static void Z() { }
                          }
                          """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        string Id(string name) => ResolveSymbolId(snapshot, $"global::Uses.{name}");
        ContextCapsule Capsule(string name, int hops) => CapsuleForId(Id(name), hops);
        ContextCapsule CapsuleForId(string id, int hops) => ContextAssembler.ResolveAndAssemble(store, store,
            new ContextLookup(snapshot, id, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, 50_000, hops), store, store);

        var coveredSymbols = new[] { Id("IState.Get"), Id("Users.Call"), Id("Users.Read") };
        var tests = new[] { Id("Coverage.Z"), Id("Coverage.Call"), Id("Coverage.Read") };
        store.SaveEdges(snapshot, coveredSymbols.Zip(tests, (covered, test) => new EdgeRecord
        {
            SourceSymbolId = covered,
            TargetSymbolId = test,
            Kind = nameof(EdgeKind.TestedBy),
            Provenance = Provenance.CompilerProved,
            ExtractorVersion = VersionConstants.ExtractorVersion
        }));

        var method = Capsule("State.Get", 3);
        Assert.Equal(Id("Users.Both"), Assert.Single(method.DirectCallers).SymbolId);
        Assert.Equal(new[] { Id("Users.Call"), Id("Users.GroupOfDispatch"), Id("Users.Group") },
            method.SecondDegreeContext.Select(item => item.SymbolId));
        Assert.All(method.SecondDegreeContext, item =>
        {
            Assert.Equal(Provenance.Possible, item.Provenance);
            Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, item.Relationship);
            Assert.False(item.Direct);
        });
        Assert.Equal("MethodGroupRef", method.SecondDegreeContext[1].EdgeKind);
        Assert.Equal("MethodGroupRef", method.SecondDegreeContext[2].EdgeKind);
        Assert.Empty(Capsule("State.Get", 1).SecondDegreeContext);
        Assert.Empty(Capsule("Derived.Get", 2).SecondDegreeContext);

        CheckDispatchAccess("State.Count", "Users.Read", "Reads");
        CheckDispatchAccess("State.Changed", "Users.Subscribe", "Writes");
        Assert.Contains(Capsule("State.Changed", 1).DirectCallers,
            item => item.SymbolId == Id("Users.DirectSubscribe") && item.EdgeKind == "Writes");
        Assert.Equal([(Id("Users.B"), "Reads"), (Id("Users.C"), "Writes")],
            Capsule("State.Value", 1).DirectCallers.Select(item => (item.SymbolId, item.EdgeKind)));

        var fieldId = Id("State.F");
        var assembly = SymbolId.Parse(fieldId).AssemblyIdentity;
        var field = CapsuleForId(fieldId, 2);
        Assert.Contains(field.DirectCallers, item => item.SymbolId == SymbolId.Compose("M:Uses.State.get_Value", assembly) &&
                                                     item.EdgeKind == "Reads");
        Assert.Contains(field.DirectCallers, item => item.SymbolId == SymbolId.Compose("M:Uses.State.set_Value(System.Int32)", assembly) &&
                                                     item.EdgeKind == "Writes");
        Assert.Contains(field.SecondDegreeContext, item => item.SymbolId == Id("Users.B") && item.EdgeKind == "Reads");
        Assert.Contains(field.SecondDegreeContext, item => item.SymbolId == Id("Users.C") && item.EdgeKind == "Writes");

        var fieldAtThree = CapsuleForId(fieldId, 3);
        var getter = CapsuleForId(SymbolId.Compose("M:Uses.State.get_Count", assembly), 2);
        var testsPage = ContextAssembler.BuildTierPage(store, store, snapshot, SymbolId.Parse(fieldId),
            "relevant_tests", 3, false, 0, 10);

        Assert.Multiple(
            () => Assert.DoesNotContain(field.SecondDegreeContext, item => item.SymbolId == Id("Users.Call") || item.SymbolId == Id("Users.Read")),
            () => AssertDispatchItem(fieldAtThree, "Users.Call", "Calls"),
            () => AssertDispatchItem(fieldAtThree, "Users.Read", "Reads"),
            () => AssertDispatchItem(getter, "Users.Read", "Reads"),
            () => Assert.Equal(tests[..1], field.RelevantTests.Select(item => item.SymbolId)),
            () => Assert.Equal(tests, fieldAtThree.RelevantTests.Select(item => item.SymbolId)),
            () => Assert.Equal(tests, testsPage.Items.Select(item => item.SymbolId)),
            () => Assert.Contains("MethodGroupRef edge", method.SecondDegreeContext[2].InclusionReason));

        void AssertDispatchItem(ContextCapsule capsule, string caller, string kind)
        {
            var item = Assert.Single(capsule.SecondDegreeContext, item => item.SymbolId == Id(caller));
            Assert.Equal(kind, item.EdgeKind);
            Assert.Equal(Provenance.Possible, item.Provenance);
            Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, item.Relationship);
            Assert.False(item.Direct);
        }

        void CheckDispatchAccess(string anchor, string caller, string kind)
        {
            var atOne = Capsule(anchor, 1);
            Assert.DoesNotContain(atOne.DirectCallers, item => item.SymbolId == Id(caller));
            Assert.Empty(atOne.SecondDegreeContext);
            var atTwo = Capsule(anchor, 2);
            Assert.DoesNotContain(atTwo.DirectCallers, item => item.SymbolId == Id(caller));
            Assert.Contains(atTwo.SecondDegreeContext, item => item.SymbolId == Id(caller) && item.EdgeKind == kind);
        }
    }

    [Fact]
    public async Task Capsule_TypeScope_ExcludesItsNestedCallersAndUnifiesOutsideCallers()
    {
        CreateProject("Uses", new Dictionary<string, string>
        {
            ["Uses.cs"] = """
                          namespace Uses;
                          public static class Target
                          {
                              public static void B() { }
                              public static void A() { }
                              public static void Inside() => A();
                              public static class Nested { public static void Inside() => A(); }
                          }
                          public static class Clients
                          {
                              public static void Both() { Target.A(); Target.B(); }
                              public static void ThroughInside() => Target.Inside();
                              public static void ViaNested() => Target.Nested.Inside();
                          }
                          """
        });
        var snapshot = await RunFullIndexAsync(DbPath);
        using var store = OpenStore(DbPath);
        var anchor = ResolveSymbolId(snapshot, "global::Uses.Target");
        var capsule = ContextAssembler.ResolveAndAssemble(store, store, new ContextLookup(snapshot, anchor, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, 50_000, 2), store, store);
        string Id(string name) => ResolveSymbolId(snapshot, $"global::Uses.Clients.{name}");
        Assert.Equal([Id("Both"), Id("ThroughInside")],
            capsule.DirectCallers.Select(item => item.SymbolId));
        Assert.Equal(Id("ViaNested"), Assert.Single(capsule.SecondDegreeContext).SymbolId);
    }

    [Fact]
    public async Task MethodGroupOnlyCallers_AppearInDirectCallersAndRelevantTests()
    {
        CreateProject("TestProject", new Dictionary<string, string> { ["MethodGroupTargets.cs"] = ProductionSource });
        CreateProject("TestProject.Tests",
            new Dictionary<string, string> { ["MethodGroupTargetTests.cs"] = TestSource },
            projectReferences: ["TestProject"],
            packageReferences: ["xunit@2.9.3"]);
        RestoreSolution();
        var snapshotId = await RunFullIndexAsync(DbPath);

        var anchorId = ResolveSymbolId(snapshotId, "global::TestProject.MethodGroupTargets.F");

        using var store = OpenStore(DbPath);
        try
        {
            var lookup = new ContextLookup(snapshotId, anchorId, null, null);
            var options = new ContextAssemblyOptions(ContextIntent.Inspect, 5000, 2);
            var capsule = ContextAssembler.ResolveAndAssemble(store, store, lookup, options, store, store);

            Assert.Contains(capsule.DirectCallers,
                item => item.FullyQualifiedName == "global::TestProject.MethodGroupTargets.Run");
            Assert.Contains(capsule.RelevantTests,
                item => item.FullyQualifiedName.Contains("MethodGroupTargetTests.Run_IsCallable", StringComparison.Ordinal));
        }
        finally
        {
            store.Close();
        }
    }
}
