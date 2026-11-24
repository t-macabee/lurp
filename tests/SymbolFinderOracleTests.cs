using Lurp.Parity.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Tests;

/// <summary>
///     Oracle B dispatch-family acceptance: Roslyn's
///     <c>SymbolFinder.FindCallersAsync</c> reports a caller of an
///     interface/abstract member also as a caller of its implementation/override,
///     and the reverse. Lurp persists the direct call as a Calls edge to the bound
///     member plus a MayDispatchTo edge between the interface or root virtual
///     member and its implementation or override; the oracle must accept a caller
///     that matches any member of the target's dispatch family, and only through
///     such a persisted MayDispatchTo edge.
/// </summary>
public sealed class SymbolFinderOracleTests
{
    private static readonly MetadataReference[] _references =
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => MetadataReference.CreateFromFile(p!))];

    private const string InterfaceImplementationSource = """
        public interface I { void M(); }

        public class C : I { public void M() { } }

        public static class A
        {
            public static void CallI(I i) { i.M(); }
        }

        public static class B
        {
            public static void CallC(C c) { c.M(); }
        }
        """;

    private const string OverrideChainSource = """
        public abstract class Base { public abstract void M(); }

        public class Mid : Base { public override void M() { } }

        public class Leaf : Mid { public override void M() { } }

        public static class Caller
        {
            public static void Call(Base b) { b.M(); }
        }
        """;

    private const string MethodGroupArgumentSource = """
        public static class Caller
        {
            public static void RunTarget() { System.Threading.Tasks.Task.Run(Target); }

            public static void Target() { }
        }
        """;

    private const string DelegateCreationSource = """
        public static class Caller
        {
            public static void RunTarget() { var a = new System.Action(Target); }

            public static void Target() { }
        }
        """;

    private const string LocalMethodGroupArgumentSource = """
        public static class Local
        {
            public static void Take(System.Action a) { }
        }

        public static class Caller
        {
            public static void RunTarget() { Local.Take(Target); }

            public static void Target() { }
        }
        """;

    private const string UsingExternalInterfaceSource = """
        public sealed class Stub : System.ComponentModel.IComponent
        {
            public System.ComponentModel.ISite Site { get; set; }
            public event System.EventHandler Disposed;
            public void Dispose() { }
        }

        public static class Caller
        {
            public static System.ComponentModel.IComponent Make() => new Stub();
            public static void UseIt() { using var s = Make(); }
        }
        """;

    private const string UsingTargetTypeSource = """
        public sealed class Stub : System.ComponentModel.IComponent
        {
            public System.ComponentModel.ISite Site { get; set; }
            public event System.EventHandler Disposed;
            public void Dispose() { }
        }

        public static class Caller
        {
            public static void UseIt() { using var s = new Stub(); }
        }
        """;

    [Fact]
    public async Task TargetImplementation_CallerOfInterfaceMatchesThroughDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var aCallI = MethodId(compilation, "A", "CallI");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (aCallI, nameof(EdgeKind.Calls), im),
            (im, nameof(EdgeKind.MayDispatchTo), cm)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, cm);

        Assert.DoesNotContain(aCallI, missing);
    }

    [Fact]
    public async Task TargetImplementation_CallerOfInterfaceWithoutDispatchEdgeIsAMiss()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var aCallI = MethodId(compilation, "A", "CallI");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (aCallI, nameof(EdgeKind.Calls), im)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, cm);

        Assert.Contains(aCallI, missing);
    }

    [Fact]
    public async Task TargetInterface_CallerOfImplementationMatchesThroughDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var bCallC = MethodId(compilation, "B", "CallC");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (bCallC, nameof(EdgeKind.Calls), cm),
            (im, nameof(EdgeKind.MayDispatchTo), cm)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, im);

        Assert.DoesNotContain(bCallC, missing);
    }

    [Fact]
    public async Task TargetInterface_CallerOfImplementationWithoutDispatchEdgeIsAMiss()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var bCallC = MethodId(compilation, "B", "CallC");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (bCallC, nameof(EdgeKind.Calls), cm)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, im);

        Assert.Contains(bCallC, missing);
    }

    [Fact]
    public async Task TargetLastOverride_CallerOfBaseMatchesThroughTransitiveDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, OverrideChainSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var caller = MethodId(compilation, "Caller", "Call");
        var baseM = MethodId(compilation, "Base", "M");
        var midM = MethodId(compilation, "Mid", "M");
        var leafM = MethodId(compilation, "Leaf", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (caller, nameof(EdgeKind.Calls), baseM),
            (baseM, nameof(EdgeKind.MayDispatchTo), midM),
            (midM, nameof(EdgeKind.MayDispatchTo), leafM)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, leafM);

        Assert.DoesNotContain(caller, missing);
    }

    [Fact]
    public async Task TargetInterfaceMember_ExternalInheritedImplementation_ConcreteCallerIsSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var external = CompileExternalReference(
            "SymbolFinderOracleExternalFixture",
            """
            namespace External;

            public class Base { public void M() { } }
            """);
        var solution = CreateSolution(
            workspace,
            """
            public interface ISolution { void M(); }

            public class Derived : External.Base, ISolution { }

            public static class Callers
            {
                public static void CallDerived(Derived d) { d.M(); }
                public static void CallInterface(ISolution s) { s.M(); }
            }
            """,
            [external]);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var isolutionM = MethodId(compilation, "ISolution", "M");
        var callDerived = MethodId(compilation, "Callers", "CallDerived");
        var callInterface = MethodId(compilation, "Callers", "CallInterface");

        var result = await SymbolFinderOracle.CompareAsync(
            solution,
            [(callInterface, nameof(EdgeKind.Calls), isolutionM)],
            SymbolDocIds.Normalized,
            new HashSet<string>(StringComparer.Ordinal) { isolutionM });

        var target = Assert.Single(result.Targets);
        Assert.Equal(isolutionM, target.TargetId);
        Assert.DoesNotContain(callInterface, target.MissingCallers.SelectMany(static missing => missing.CallerIds));

        // The concrete caller binds to External.Base.M, which is outside the
        // solution: it is set aside, not counted as a miss.
        var setAside = Assert.Single(result.Summary.SetAsideCallers);
        Assert.Equal(SymbolFinderOracle.BindsExternalReason, setAside.Reason);
        Assert.Contains(callDerived, setAside.CallerIds);
    }

    [Fact]
    public async Task TargetImplementation_ExternalInterfaceMember_IsCheckedNotSkipped()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(
            workspace,
            """
            public class C : System.IDisposable
            {
                public void Dispose() { }
            }
            """);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var dispose = MethodId(compilation, "C", "Dispose");

        var result = await RunAsync(solution, [], dispose);

        // The external implementation no longer hides the target; with no
        // callers it is checked and produces no miss.
        Assert.Contains(result.Targets, target => target.TargetId == dispose);
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetDispose_UsingOfExternalInterface_IsSetAsideAsBindsExternal()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, UsingExternalInterfaceSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var dispose = MethodId(compilation, "Stub", "Dispose");
        var useIt = MethodId(compilation, "Caller", "UseIt");

        var result = await RunAsync(solution, [], dispose);

        // The using binds IComponent.Dispose through the interface return type,
        // which is outside the solution: set aside, not a miss on Stub.Dispose.
        var target = Assert.Single(result.Targets);
        Assert.Equal(dispose, target.TargetId);
        Assert.DoesNotContain(useIt, target.MissingCallers.SelectMany(static missing => missing.CallerIds));

        var setAside = Assert.Single(result.Summary.SetAsideCallers);
        Assert.Equal(SymbolFinderOracle.BindsExternalReason, setAside.Reason);
        Assert.Contains(useIt, setAside.CallerIds);
    }

    [Fact]
    public async Task TargetDispose_UsingOfTargetType_IsExpected()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, UsingTargetTypeSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var dispose = MethodId(compilation, "Stub", "Dispose");
        var useIt = MethodId(compilation, "Caller", "UseIt");

        var result = await RunAsync(solution, [], dispose);

        // The using binds Stub.Dispose directly: expected, and with no edge it
        // stays a miss.
        var target = Assert.Single(result.Targets);
        Assert.Equal(dispose, target.TargetId);
        Assert.Contains(useIt, target.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.DoesNotContain(result.Summary.SetAsideCallers, entry => entry.CallerIds.Contains(useIt));
    }

    [Fact]
    public async Task TargetImplementation_MetadataInterfaceFromSecondProject_CallerThroughInterfaceIsExpected()
    {
        using var workspace = new AdhocWorkspace();
        var interfaceImage = CompileExternalReference(
            "SymbolFinderOracleMetadataInterface",
            "public interface I { void M(); }");
        var solution = CreateTwoProjectSolution(
            workspace,
            "public interface I { void M(); }",
            """
            public class C : I { public void M() { } }

            public static class BCaller
            {
                public static void Call(I i) { i.M(); }
            }
            """,
            interfaceImage);
        var projectB = solution.Projects.Single(project => project.Name == "ProjectB");
        var compilationB = await projectB.GetCompilationAsync()
                           ?? throw new InvalidOperationException("Compilation not loaded.");
        var cm = MethodId(compilationB, "C", "M");
        var bCaller = MethodId(compilationB, "BCaller", "Call");

        // The interface member as project B sees it: a metadata view from the
        // first project's image, whose containing-assembly object differs from
        // the source project's assembly.
        var metadataI = compilationB.GetTypeByMetadataName("I")
                        ?? throw new InvalidOperationException("Metadata interface not found.");
        var metadataIM = metadataI.GetMembers("M").OfType<IMethodSymbol>().Single();
        var metadataIMId = SymbolDocIds.Normalized(metadataIM)
                           ?? throw new InvalidOperationException("No doc id for metadata I.M.");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (bCaller, nameof(EdgeKind.Calls), metadataIMId),
            (metadataIMId, nameof(EdgeKind.MayDispatchTo), cm)
        };

        var result = await SymbolFinderOracle.CompareAsync(
            solution,
            edges,
            SymbolDocIds.Normalized,
            new HashSet<string>(StringComparer.Ordinal) { cm });

        var target = Assert.Single(result.Targets);
        Assert.Equal(cm, target.TargetId);
        Assert.Empty(target.MissingCallers);
        Assert.DoesNotContain(result.Summary.SetAsideCallers,
            entry => entry.CallerIds.Contains(bCaller));
    }

    [Fact]
    public async Task TargetInternalImplementation_IsNotSkipped()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var cm = MethodId(compilation, "C", "M");

        var result = await RunAsync(solution, [], cm);

        // C.M is in the solution, so it is checked; the interface caller binds to
        // I.M, which is in solution but outside C.M's dispatch family {C.M} and is
        // recorded as binds_other (still checked, so it stays a miss).
        Assert.DoesNotContain(result.Summary.SetAsideCallers,
            entry => entry.Reason == SymbolFinderOracle.BindsExternalReason);
        var entry = Assert.Single(result.Summary.SetAsideCallers);
        Assert.Equal(SymbolFinderOracle.BindsOtherReason, entry.Reason);
        Assert.Equal(["M:A.CallI(I)"], entry.CallerIds);
        var target = Assert.Single(result.Targets);
        Assert.Equal(cm, target.TargetId);
        Assert.Contains(target.MissingCallers, missing => missing.CallerIds.Contains("M:A.CallI(I)"));
    }

    [Fact]
    public async Task TargetMethodGroup_PassedToExternalCall_MatchesThroughMethodGroupRefEdge()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, MethodGroupArgumentSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (runTarget, nameof(EdgeKind.MethodGroupRef), target)
        };

        var result = await RunAsync(solution, edges, target);

        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(target, targetResult.TargetId);
        Assert.DoesNotContain(runTarget, targetResult.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetMethodGroup_PassedToExternalCallWithoutEdge_IsAMissNotSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, MethodGroupArgumentSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var result = await RunAsync(solution, [], target);

        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(target, targetResult.TargetId);
        Assert.Contains(runTarget, targetResult.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetMethodGroup_InDelegateCreationWithoutEdge_IsAMissNotSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, DelegateCreationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var result = await RunAsync(solution, [], target);

        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(target, targetResult.TargetId);
        Assert.Contains(runTarget, targetResult.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetMethodGroup_PassedToLocalCall_IsNotSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, LocalMethodGroupArgumentSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var result = await RunAsync(solution, [], target);

        Assert.DoesNotContain(result.Summary.SetAsideCallers, entry => entry.CallerIds.Contains(runTarget));
    }

    [Fact]
    public async Task TargetExplicitImplementation_CallerThroughInterfaceMatchesThroughDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public interface I { void M(); }
            public class Ex : I { void I.M() { } }
            public static class K { public static void Call(I i) { i.M(); } }
            """);
        const string target = "M:Ex.I#M";
        Assert.Empty(await MissingCallerIdsAsync(solution,
            [("M:K.Call(I)", "Calls", "M:I.M"), ("M:I.M", "MayDispatchTo", target)], target));
        Assert.Equal(["M:K.Call(I)"], await MissingCallerIdsAsync(solution,
            [("M:K.Call(I)", "Calls", "M:I.M")], target));
    }

    [Theory]
    [InlineData("add_E", "M:K.Sub(F)", "M:K.Nested(F)")]
    [InlineData("remove_E", "M:K.Unsub(F,System.Action)", null)]
    public async Task TargetFieldLikeEventAccessor_MatchesThroughWritesOnTheEvent(
        string accessor, string firstCaller, string? secondCaller)
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public class F { public event System.Action? E; }
            public static class K
            {
                public static void Sub(F f) { f.E += () => { }; }
                public static void Unsub(F f, System.Action h) { f.E -= h; }
                public static void Nested(F f) { System.Action a = () => { f.E += () => { }; }; a(); }
            }
            """);
        string[] callers = secondCaller is null ? [firstCaller] : [firstCaller, secondCaller];
        var target = $"M:F.{accessor}(System.Action)";
        Assert.Empty(await MissingCallerIdsAsync(solution,
            callers.Select(caller => (caller, "Writes", "E:F.E")).ToArray(), target));
        Assert.Equal(callers.Order(StringComparer.Ordinal),
            (await MissingCallerIdsAsync(solution, [], target)).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    public async Task TargetStaticAbstractImplementation_CallerThroughTypeParameterMatchesThroughDispatchFamily(string typeKind)
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, $$"""
            public interface IP<T> where T : IP<T> { static abstract T Parse(string s); }
            public {{typeKind}} C : IP<C>
            {
                public static C Parse(string s) => new();
                public static C Parse(string s, int i) => new();
            }
            public static class K
            {
                public static T G<T>() where T : IP<T> => T.Parse("x");
                public static void P() { C.Parse("x", 1); }
            }
            """);
        const string target = "M:C.Parse(System.String)";
        const string member = "M:IP`1.Parse(System.String)";
        Assert.Empty(await MissingCallerIdsAsync(solution,
            [("M:K.G``1", "Calls", member), (member, "MayDispatchTo", target)], target));
        Assert.Equal(["M:K.G``1"], await MissingCallerIdsAsync(solution, [], target));
        Assert.Equal(["M:K.P"], await MissingCallerIdsAsync(solution, [], "M:C.Parse(System.String,System.Int32)"));
    }

    [Fact]
    public async Task TargetDispose_UsingThroughInterfaceTypedLocal_IsSetAsideAsBindsExternal()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public sealed class X : System.IDisposable { public void Dispose() { } }
            public static class K { public static void U1() { using System.IDisposable r = new X(); } }
            """);
        var result = await RunAsync(solution, [], "M:X.Dispose");
        Assert.Empty(Assert.Single(result.Targets).MissingCallers);
        var entry = Assert.Single(result.Summary.SetAsideCallers);
        Assert.Equal("binds_external", entry.Reason);
        Assert.Equal(["M:K.U1"], entry.CallerIds);
    }

    [Fact]
    public async Task TargetConstructor_DynamicArgument_IsSetAsideAsDynamicBinding()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public sealed class Y { public Y(int a) { } public Y(string s) { } }
            public static class K { public static void D(dynamic d) { var y = new Y(d); } }
            """);
        string[] ids = ["M:Y.#ctor(System.Int32)", "M:Y.#ctor(System.String)"];
        var result = await SymbolFinderOracle.CompareAsync(solution, [], SymbolDocIds.Normalized, ids.ToHashSet());
        Assert.Equal(ids, result.Targets.Select(target => target.TargetId).Order(StringComparer.Ordinal));
        Assert.All(result.Targets, target => Assert.Empty(target.MissingCallers));
        Assert.Equal(ids, result.Summary.SetAsideCallers.Select(entry => entry.TargetId).Order(StringComparer.Ordinal));
        Assert.All(result.Summary.SetAsideCallers, entry =>
        {
            Assert.Equal("dynamic_binding", entry.Reason);
            Assert.Equal(["M:K.D(System.Object)"], entry.CallerIds);
        });
    }

    [Fact]
    public async Task TargetCalledInsideDynamicArguments_IsNotSetAsideAsDynamicBinding()
    {
        // The call T() binds statically; only the outer call d.Foo(...) is dynamic.
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public static class K
            {
                public static int T() => 1;
                public static void D(dynamic d) { d.Foo(T()); }
            }
            """);
        var result = await SymbolFinderOracle.CompareAsync(
            solution, [], SymbolDocIds.Normalized, new HashSet<string> { "M:K.T" });
        Assert.Empty(result.Summary.SetAsideCallers);
        var miss = Assert.Single(Assert.Single(result.Targets).MissingCallers);
        Assert.Equal(["M:K.D(System.Object)"], miss.CallerIds);
    }

    [Fact]
    public async Task MissReport_ListsExactlyTheAcceptedRelations()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public sealed class Y { public Y(int a) { } }
            public static class K { public static Y Make() => new Y(1); }
            public class G { public event System.Action E { add { } remove { } } }
            public static class K2 { public static void Sub(G g) { g.E += () => { }; } }
            """);
        var constructor = await RunAsync(solution, [], "M:Y.#ctor(System.Int32)");
        Assert.Equal(["Calls", "MethodGroupRef"],
            Assert.Single(Assert.Single(constructor.Targets).MissingCallers).MissingEdgeKinds);
        var add = await RunAsync(solution, [], "M:G.add_E(System.Action)");
        Assert.Equal(["Calls", "MethodGroupRef", "Writes"],
            Assert.Single(Assert.Single(add.Targets).MissingCallers).MissingEdgeKinds);
    }

    [Fact]
    public async Task TargetPropertyGet_AcceptsReadsNotWrites()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public class P { public int V { get; set; } }
            public static class K { public static int Read(P p) => p.V; }
            """);
        Assert.Empty(await MissingCallerIdsAsync(solution, [("M:K.Read(P)", "Reads", "P:P.V")], "M:P.get_V"));
        Assert.Equal(["M:K.Read(P)"], await MissingCallerIdsAsync(solution,
            [("M:K.Read(P)", "Writes", "P:P.V")], "M:P.get_V"));
    }

    [Fact]
    public async Task TargetExplicitPropertyGetter_AcceptsReadsThroughOwnerDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public interface I { int Value { get; } }
            public sealed class C : I { int I.Value => 1; }
            public static class Reader { public static int Read(I value) => value.Value; }
            """);
        var compilation = (await solution.Projects.Single().GetCompilationAsync())!;
        var implementation = compilation.Assembly.GlobalNamespace.GetTypeMembers("C").Single()
            .GetMembers().OfType<IPropertySymbol>().Single();
        var target = SymbolDocIds.Normalized(implementation.GetMethod!)!;
        var owner = SymbolDocIds.Normalized(implementation)!;
        const string caller = "M:Reader.Read(I)";
        var read = (caller, nameof(EdgeKind.Reads), "P:I.Value");

        Assert.Equal([caller], await MissingCallerIdsAsync(solution, [read], target));
        Assert.Empty(await MissingCallerIdsAsync(solution,
            [read, ("P:I.Value", nameof(EdgeKind.MayDispatchTo), owner)], target));
    }

    [Fact]
    public async Task TargetPropertySet_AcceptsWrites()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public class P { public int V { get; set; } }
            public static class K { public static void Write(P p) { p.V = 1; } }
            """);
        const string target = "M:P.set_V(System.Int32)";
        Assert.Empty(await MissingCallerIdsAsync(solution, [("M:K.Write(P)", "Writes", "P:P.V")], target));
        Assert.Equal(["M:K.Write(P)"], await MissingCallerIdsAsync(solution, [], target));
    }

    [Fact]
    public async Task CallerInAutoPropertyInitializer_NormalizesToTheProperty()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public class Q { public int W { get; } = Helper(); public static int Helper() => 1; }
            """);
        Assert.Empty(await MissingCallerIdsAsync(solution, [("P:Q.W", "Calls", "M:Q.Helper")], "M:Q.Helper"));
        Assert.Contains("P:Q.W", await MissingCallerIdsAsync(solution, [], "M:Q.Helper"));
    }

    [Fact]
    public async Task CallerInLocalFunction_NormalizesToTheContainingMethod()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, """
            public static class L { public static void Outer() { Inner(); void Inner() => Target(); } public static void Target() { } }
            """);
        Assert.Empty(await MissingCallerIdsAsync(solution, [("M:L.Outer", "Calls", "M:L.Target")], "M:L.Target"));
    }

    [Fact]
    public async Task TargetIdsFilter_EvaluatesOnlyTheListedTargets()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        Assert.Equal("M:C.M", Assert.Single((await RunAsync(solution, [], "M:C.M")).Targets).TargetId);
    }

    private static async Task<SymbolFinderOracleResult> RunAsync(
        Solution solution,
        IReadOnlyList<(string Source, string Kind, string Target)> edges,
        string targetId)
    {
        return await SymbolFinderOracle.CompareAsync(
            solution,
            edges,
            SymbolDocIds.Normalized,
            new HashSet<string>(StringComparer.Ordinal) { targetId });
    }

    private static PortableExecutableReference CompileExternalReference(string assemblyName, string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
            throw new InvalidOperationException(
                "External fixture did not compile: " + string.Join("; ", emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray().ToImmutableArray());
    }

    private static async Task<IReadOnlyList<string>> MissingCallerIdsAsync(
        Solution solution,
        IReadOnlyList<(string Source, string Kind, string Target)> edges,
        string targetId)
    {
        var result = await SymbolFinderOracle.CompareAsync(
            solution,
            edges,
            SymbolDocIds.Normalized,
            new HashSet<string>(StringComparer.Ordinal) { targetId });

        var target = Assert.Single(result.Targets);
        Assert.Equal(targetId, target.TargetId);
        return [.. target.MissingCallers.SelectMany(static missing => missing.CallerIds)];
    }

    private static string MethodId(Compilation compilation, string typeName, string methodName)
    {
        var type = compilation.Assembly.GlobalNamespace.GetTypeMembers(typeName).Single();
        var method = type.GetMembers(methodName).OfType<IMethodSymbol>().Single();
        return SymbolDocIds.Normalized(method)
               ?? throw new InvalidOperationException($"No doc id for {typeName}.{methodName}.");
    }

    private static Solution CreateSolution(
        AdhocWorkspace workspace,
        string source,
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var solutionId = SolutionId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), "SymbolFinderOracleTests.slnx"));

        var references = new List<MetadataReference>(_references);
        if (extraReferences is not null)
            references.AddRange(extraReferences);

        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "TestProject",
                "TestProject",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddDocument(
                RoslynDocumentId.CreateNewId(projectId),
                "Source.cs",
                SourceText.From(source, Encoding.UTF8));

        if (!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not apply the test solution.");

        return workspace.CurrentSolution;
    }

    private static Solution CreateTwoProjectSolution(
        AdhocWorkspace workspace,
        string sourceA,
        string sourceB,
        MetadataReference referenceToA)
    {
        var solutionId = SolutionId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), "SymbolFinderOracleTests.slnx"));

        var projectAId = ProjectId.CreateNewId();
        var projectBId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectAId,
                VersionStamp.Create(),
                "ProjectA",
                "ProjectA",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: _references))
            .AddProject(ProjectInfo.Create(
                projectBId,
                VersionStamp.Create(),
                "ProjectB",
                "ProjectB",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [.. _references, referenceToA]))
            .AddDocument(RoslynDocumentId.CreateNewId(projectAId), "A.cs", SourceText.From(sourceA, Encoding.UTF8))
            .AddDocument(RoslynDocumentId.CreateNewId(projectBId), "B.cs", SourceText.From(sourceB, Encoding.UTF8));

        if (!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not apply the test solution.");

        return workspace.CurrentSolution;
    }
}
