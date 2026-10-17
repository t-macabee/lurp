using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     Phase 1 golden tests: one test per compiler-proved edge kind, asserting the
///     exact edges a known source produces. Pattern B (in-memory compilation).
/// </summary>
public sealed class GoldenEdgeTests : InMemoryTestBase
{
    private const string Doc = "Source.cs";

    private static Dictionary<string, string> One(string source)
    {
        return new Dictionary<string, string> { [Doc] = source };
    }

    private static void AssertEdgeContract(EdgeRecord edge, string kind, string provenance, string extractorVersion,
        string sourceFqnEndsWith)
    {
        Assert.Equal(kind, edge.Kind);
        Assert.Equal(provenance, edge.Provenance);
        Assert.Equal(extractorVersion, edge.ExtractorVersion);
        Assert.NotNull(edge.SourceDocumentPath);
        Assert.EndsWith(sourceFqnEndsWith, edge.SourceDocumentPath);
    }

    [Fact]
    public async Task CallsEdge_MethodCallsMethod()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Calculator
                                                {
                                                    public int Add(int a, int b) { return a + b; }
                                                    public int Compute(int x, int y) { return Add(x, y); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Calls", "global::N.Calculator.Compute", "global::N.Calculator.Add");
        AssertEdgeContract(edge, "Calls", Provenance.CompilerProved, "calls-v2", Doc);
    }

    [Fact]
    public async Task ConstructsEdge_MethodConstructsType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Widget { }
                                                public class Factory
                                                {
                                                    public Widget Create() { return new Widget(); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Constructs", "global::N.Factory.Create", "global::N.Widget");
        AssertEdgeContract(edge, "Constructs", Provenance.CompilerProved, "constructs-v1", Doc);
    }

    [Fact]
    public async Task InheritsEdge_DerivedInheritsBase()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Base { }
                                                public class Derived : Base { }
                                                """));

        var edge = extraction.SingleEdge("Inherits", "global::N.Derived", "global::N.Base");
        AssertEdgeContract(edge, "Inherits", Provenance.CompilerProved, VersionConstants.ExtractorVersion, Doc);
    }

    [Fact]
    public async Task ImplementsEdge_ClassImplementsInterface()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public interface IService { void Do(); }
                                                public class Service : IService { public void Do() { } }
                                                """));

        var edge = extraction.SingleEdge("Implements", "global::N.Service", "global::N.IService");
        AssertEdgeContract(edge, "Implements", Provenance.CompilerProved, VersionConstants.ExtractorVersion, Doc);
    }

    [Fact]
    public async Task OverridesEdge_MethodOverridesVirtualBase()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Base { public virtual void Do() { } }
                                                public class Derived : Base { public override void Do() { } }
                                                """));

        var edge = extraction.SingleEdge("Overrides", "global::N.Derived.Do", "global::N.Base.Do");
        AssertEdgeContract(edge, "Overrides", Provenance.CompilerProved, "overrides-v1", Doc);
    }

    [Fact]
    public async Task HidesEdge_MethodHidesBaseMember()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Base { public void Do() { } }
                                                public class Derived : Base { public new void Do() { } }
                                                """));

        var edge = extraction.SingleEdge("Hides", "global::N.Derived.Do", "global::N.Base.Do");
        AssertEdgeContract(edge, "Hides", Provenance.CompilerProved, "hides-v1", Doc);
    }

    [Fact]
    public async Task ReadsEdge_MethodReadsField()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Counter
                                                {
                                                    private int _value;
                                                    public int GetValue() { return _value; }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Reads", "global::N.Counter.GetValue", "global::N.Counter._value");
        AssertEdgeContract(edge, "Reads", Provenance.CompilerProved, "readswrites-v1", Doc);
    }

    [Fact]
    public async Task WritesEdge_MethodWritesField()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Counter
                                                {
                                                    private int _value;
                                                    public void SetValue(int v) { _value = v; }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Writes", "global::N.Counter.SetValue", "global::N.Counter._value");
        AssertEdgeContract(edge, "Writes", Provenance.CompilerProved, "readswrites-v1", Doc);
    }

    [Fact]
    public async Task ReadsWrites_MemberAccessWrite_EmitsNoRead()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target
                                                {
                                                    public int P { get; set; }
                                                    public int F;
                                                }
                                                public class User
                                                {
                                                    private int _g;
                                                    public void Set(Target t) { t.P = 1; this._g = 2; t.F = 3; }
                                                    public void SetConditional(Target? t) { t?.P = 4; }
                                                }
                                                """));

        extraction.SingleEdge("Writes", "global::N.User.Set", "global::N.Target.P");
        extraction.SingleEdge("Writes", "global::N.User.Set", "global::N.User._g");
        extraction.SingleEdge("Writes", "global::N.User.Set", "global::N.Target.F");
        extraction.SingleEdge("Writes", "global::N.User.SetConditional", "global::N.Target.P");

        var set = extraction.ResolveId("global::N.User.Set");
        var setConditional = extraction.ResolveId("global::N.User.SetConditional");
        var writtenTargets = new[]
        {
            extraction.ResolveId("global::N.Target.P"),
            extraction.ResolveId("global::N.User._g"),
            extraction.ResolveId("global::N.Target.F")
        };

        var reads = extraction.EdgesOf("Reads")
            .Where(e => (e.SourceSymbolId == set || e.SourceSymbolId == setConditional)
                        && writtenTargets.Contains(e.TargetSymbolId))
            .ToList();
        Assert.Empty(reads);
    }

    [Fact]
    public async Task ReturnsEdge_MethodReturnsType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Result { }
                                                public class Producer
                                                {
                                                    public Result Produce() { return new Result(); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Returns", "global::N.Producer.Produce", "global::N.Result");
        AssertEdgeContract(edge, "Returns", Provenance.CompilerProved, "returns-v1", Doc);
    }

    [Fact]
    public async Task ThrowsEdge_MethodThrowsExceptionType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Guard
                                                {
                                                    public void Validate(string? value)
                                                    {
                                                        if (value == null) throw new System.ArgumentNullException(nameof(value));
                                                    }
                                                }
                                                """));

        var throws = extraction.EdgesOf("Throws");
        var edge = Assert.Single(throws);
        Assert.Equal(extraction.ResolveId("global::N.Guard.Validate"), edge.SourceSymbolId);
        Assert.StartsWith("T:System.ArgumentNullException|", edge.TargetSymbolId);
        AssertEdgeContract(edge, "Throws", Provenance.CompilerProved, "throws-v1", Doc);
    }

    [Fact]
    public async Task DeclaresEdge_TypeDeclaresMethodAndField()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Container
                                                {
                                                    public void Method() { }
                                                    private int _field;
                                                }
                                                """));

        var method = extraction.SingleEdge("Declares", "global::N.Container", "global::N.Container.Method");
        AssertEdgeContract(method, "Declares", Provenance.CompilerProved, "declares-v1", Doc);

        var field = extraction.SingleEdge("Declares", "global::N.Container", "global::N.Container._field");
        AssertEdgeContract(field, "Declares", Provenance.CompilerProved, "declares-v1", Doc);
    }

    [Fact]
    public async Task ParameterDependenciesEdge_CtorParameterReferencesType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Logger { }
                                                public class Service
                                                {
                                                    public Service(Logger logger) { }
                                                }
                                                """));

        // Explicitly-declared constructors surface as method declarations whose
        // FQN is the containing type name twice: global::N.Service.Service.
        var edge = extraction.SingleEdge("References", "global::N.Service.Service", "global::N.Logger");
        AssertEdgeContract(edge, "References", Provenance.CompilerProved, "parameter-deps-v1", Doc);
    }

    [Fact]
    public async Task ParameterDependenciesEdge_DelegateParameterReferencesTypeFromDelegate()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Payload { }
                                                public class Result { }
                                                public delegate Result Handler(Payload p, int n);
                                                """));

        // The delegate type owns its signature: a delegate's implicit Invoke names its
        // parameter types in the delegate declaration, so the edge source is Handler.
        var edge = extraction.SingleEdge("References", "global::N.Handler", "global::N.Payload");
        AssertEdgeContract(edge, "References", Provenance.CompilerProved, "parameter-deps-v1", Doc);

        var invokeId = extraction.ResolveId("global::N.Handler.Invoke");
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "References" && e.SourceSymbolId == invokeId);
    }

    [Fact]
    public async Task OperationShapesEdge_LocalFunctionSignatureReferencesTypesFromEnclosingMember()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class P { }
                                                public class R { }
                                                public class Host
                                                {
                                                    public void M() { static R? L(P? p) => null; }
                                                }
                                                """));

        // B58: a local function is not a snapshot symbol, so its parameter and return
        // types are References edges from the enclosing member.
        var fromParameter = extraction.SingleEdge("References", "global::N.Host.M", "global::N.P");
        AssertEdgeContract(fromParameter, "References", Provenance.CompilerProved, "operation-shapes-v1", Doc);

        var fromReturn = extraction.SingleEdge("References", "global::N.Host.M", "global::N.R");
        AssertEdgeContract(fromReturn, "References", Provenance.CompilerProved, "operation-shapes-v1", Doc);

        // A local function is neither a declared symbol nor a type, so it can be neither
        // the source nor the target of a References edge.
        var declaredIds = extraction.FqnToSymbolId.Values.ToHashSet();
        Assert.All(extraction.EdgesOf("References"),
            e =>
            {
                Assert.Contains(e.SourceSymbolId, declaredIds);
                Assert.Contains(e.TargetSymbolId, declaredIds);
            });
    }

    [Fact]
    public async Task ParameterDependenciesEdge_ExtensionBlockReceiverReferencesType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class P { }
                                                public static class PExtensions
                                                {
                                                    extension(P p)
                                                    {
                                                        public int Size() => 0;
                                                        public int Length => 0;
                                                        public static int Make() => 0;
                                                    }
                                                }
                                                """));

        // B58: a block member's receiver is declared on the block, not in the member's
        // parameter list, so the edge comes from the outer-class implementation method.
        foreach (var implementation in new[] { "global::N.PExtensions.Size", "global::N.PExtensions.get_Length", "global::N.PExtensions.Make" })
        {
            var edge = extraction.SingleEdge("References", implementation, "global::N.P");
            AssertEdgeContract(edge, "References", Provenance.CompilerProved, "parameter-deps-v1", Doc);
        }
    }

    [Fact]
    public async Task VoidReturns_AreNotReferencesTargets()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class H
                                                {
                                                    public void M() { static void L() { } }
                                                }
                                                """));

        // B61: void names no type, so neither M's return nor the local function's return
        // may become a References target.
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "References" && e.TargetSymbolId.Contains("System.Void"));
    }

    [Fact]
    public async Task OperationShapesEdge_TypedLambdaParameterReferencesTypeFromEnclosingMember()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class P { }
                                                public class R { }
                                                public static class Host
                                                {
                                                    public static void Take(System.Delegate d) { }
                                                    public static void M() { Take((P p) => 1); Take(R? () => null); }
                                                }
                                                """));

        // B63: a lambda names its explicitly typed parameters and its explicit return type in the
        // source, so the enclosing member is the References source for each of them.
        var fromParameter = extraction.SingleEdge("References", "global::N.Host.M", "global::N.P");
        AssertEdgeContract(fromParameter, "References", Provenance.CompilerProved, "operation-shapes-v1", Doc);

        var fromReturn = extraction.SingleEdge("References", "global::N.Host.M", "global::N.R");
        AssertEdgeContract(fromReturn, "References", Provenance.CompilerProved, "operation-shapes-v1", Doc);
    }

    [Fact]
    public async Task OperationShapesEdge_ImplicitlyTypedLambdaParameterAddsNoReference()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Q { }
                                                public static class Host
                                                {
                                                    public static void Run(System.Func<Q, int> f) { }
                                                    public static void M() { Run(q => 1); }
                                                }
                                                """));

        // B63 (D2): an implicitly typed parameter is not named in the source, so M gets no edge to Q.
        // Run's own signature names Q, but that edge starts at Run, not at M.
        var mId = extraction.ResolveId("global::N.Host.M");
        var qId = extraction.ResolveId("global::N.Q");
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "References" && e.SourceSymbolId == mId && e.TargetSymbolId == qId);
    }

    [Fact]
    public async Task ExtensionReceiverEdge_ReceiverTypeBindsToExtensionMethod()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target { }
                                                public static class TargetExtensions
                                                {
                                                    public static void Extend(this Target t) { }
                                                }
                                                public class Consumer
                                                {
                                                    public void Use(Target t) { t.Extend(); }
                                                }
                                                """));

        var receiver =
            extraction.SingleEdge("ExtensionReceiver", "global::N.Target", "global::N.TargetExtensions.Extend");
        AssertEdgeContract(receiver, "ExtensionReceiver", Provenance.CompilerProved, "extension-receiver-v1", Doc);

        var call = extraction.SingleEdge("Calls", "global::N.Consumer.Use", "global::N.TargetExtensions.Extend");
        AssertEdgeContract(call, "Calls", Provenance.CompilerProved, "calls-v2", Doc);
    }

    [Fact]
    public async Task MayDispatchToEdge_InterfaceMethodDispatchesToImplementation()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public interface IService { void Execute(); }
                                                public class Service : IService { public void Execute() { } }
                                                """));

        var edge = extraction.SingleEdge("MayDispatchTo", "global::N.IService.Execute", "global::N.Service.Execute");
        AssertEdgeContract(edge, "MayDispatchTo", Provenance.CompilerProved, "polymorphism-v1", Doc);
    }

    [Fact]
    public async Task StaticallyCallsEdge_InterfaceCallSiteRecordsStaticDispatch()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public interface IRepo { void Save(); }
                                                public class Repo : IRepo { public void Save() { } }
                                                public class Handler
                                                {
                                                    public void Handle(IRepo repo) { repo.Save(); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("StaticallyCalls", "global::N.Handler.Handle", "global::N.IRepo.Save");
        AssertEdgeContract(edge, "StaticallyCalls", Provenance.CompilerProved, "statically-calls-v1", Doc);

        // The same call site also carries a plain Calls edge to the interface method.
        Assert.Single(extraction.EdgesOf("Calls"), e =>
            e.SourceSymbolId == extraction.ResolveId("global::N.Handler.Handle") &&
            e.TargetSymbolId == extraction.ResolveId("global::N.IRepo.Save"));
    }

    [Fact]
    public async Task ContainsEdge_OuterTypeContainsNestedType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Outer
                                                {
                                                    public class Inner { }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Contains", "global::N.Outer", "global::N.Outer.Inner");
        AssertEdgeContract(edge, "Contains", Provenance.CompilerProved, VersionConstants.ExtractorVersion, Doc);
    }

    [Fact]
    public async Task ComprehensiveSource_ProducesAllEightEdgeKinds()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public interface ICalculator
                                                {
                                                    int Add(int a, int b);
                                                }
                                                public class Calculator : ICalculator
                                                {
                                                    private int _total;
                                                    public int Total => _total;
                                                    public int Add(int a, int b)
                                                    {
                                                        _total = a + b;
                                                        return _total;
                                                    }
                                                    public Calculator Create() => new Calculator();
                                                    public void Validate(ICalculator calc)
                                                    {
                                                        if (calc == null) throw new System.ArgumentNullException(nameof(calc));
                                                    }
                                                }
                                                """));

        Assert.NotEmpty(extraction.EdgesOf("Implements"));
        Assert.NotEmpty(extraction.EdgesOf("Declares"));
        Assert.NotEmpty(extraction.EdgesOf("Reads"));
        Assert.NotEmpty(extraction.EdgesOf("Writes"));
        Assert.NotEmpty(extraction.EdgesOf("Constructs"));
        Assert.NotEmpty(extraction.EdgesOf("Returns"));
        Assert.NotEmpty(extraction.EdgesOf("Throws"));
        // Calls: the interface member Add is never invoked from source; the
        // implicit ctor is not invoked either, so no Calls edge is expected.
    }

    [Fact]
    public async Task PrimaryConstructorBase_AmbiguousBinding_EmitsNoCallAndRecordsIncompleteness()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Base
                                                {
                                                    public Base(int a, long b) { }
                                                    public Base(long a, int b) { }
                                                }
                                                public class Derived(int x) : Base(x, x) { }
                                                """));

        var baseConstructorCalls = extraction.Result.Edges
            .Where(e => e.Kind == "Calls"
                        && e.SourceSymbolId.StartsWith("M:N.Derived.#ctor", StringComparison.Ordinal)
                        && e.TargetSymbolId.StartsWith("M:N.Base.#ctor", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(baseConstructorCalls);

        Assert.Contains(extraction.Result.BindingIncompleteness,
            r => r.DocumentPath?.EndsWith(Doc, StringComparison.Ordinal) == true && r.Reason == "ambiguous_overload");
    }

    [Fact]
    public async Task ParameterDependenciesEdge_MethodParameterUsesTheParameterTypeLocation()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Foo { }
                                                public class Bar { }
                                                public class Host
                                                {
                                                    public void M(Foo a, Bar b) { }
                                                }
                                                """));

        // The edge names the parameter's type, so its location is that type's syntax
        // (`Foo` / `Bar`), not the member span: a body edit must not move it.
        var foo = extraction.SingleEdge("References", "global::N.Host.M", "global::N.Foo");
        Assert.Equal((5, 18, 5, 21), (foo.SourceStartLine, foo.SourceStartColumn, foo.SourceEndLine, foo.SourceEndColumn));

        var bar = extraction.SingleEdge("References", "global::N.Host.M", "global::N.Bar");
        Assert.Equal((5, 25, 5, 28), (bar.SourceStartLine, bar.SourceStartColumn, bar.SourceEndLine, bar.SourceEndColumn));
    }

    [Fact]
    public async Task ParameterDependenciesEdge_PrimaryConstructorParameterUsesTheTypeLocation()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Foo { }
                                                public class Bar { }
                                                public class Host(Foo a, Bar b) { }
                                                """));

        // A primary constructor has no parameter-list syntax of its own in Roslyn's
        // declaring-syntax view (it reports the type declaration); the edge still
        // points at the type inside the primary parameter list.
        var foo = extraction.SingleEdge("References", "global::N.Host.Host", "global::N.Foo");
        Assert.Equal((3, 18, 3, 21), (foo.SourceStartLine, foo.SourceStartColumn, foo.SourceEndLine, foo.SourceEndColumn));

        var bar = extraction.SingleEdge("References", "global::N.Host.Host", "global::N.Bar");
        Assert.Equal((3, 25, 3, 28), (bar.SourceStartLine, bar.SourceStartColumn, bar.SourceEndLine, bar.SourceEndColumn));
    }

    [Fact]
    public async Task ParameterDependenciesEdge_AddingAMethodBelow_DoesNotMoveTheEdgesOfM()
    {
        const string before = """
                              namespace N;
                              public class Foo { }
                              public class Host(Foo a)
                              {
                                  public void M(Foo b) { }
                              }
                              """;
        // The new member lengthens the type but leaves the parameter lists alone.
        const string after = """
                             namespace N;
                             public class Foo { }
                             public class Host(Foo a)
                             {
                                 public void M(Foo b) { }
                                 public void Added(int x) { }
                             }
                             """;

        var from = await ExtractAsync(One(before));
        var to = await ExtractAsync(One(after));

        var dbPath = Path.Combine(Path.GetTempPath(), $"lurp-r142-diff-{Guid.NewGuid():N}.db");
        try
        {
            using var store = new SqliteIndexStore(dbPath);
            store.Open();
            store.RunMigrations();
            store.SaveEdges("snap-from", from.Result.Edges);
            store.SaveEdges("snap-to", to.Result.Edges);

            var (changes, _) = new SemanticDiffer(store, store, store).ComputeDiff("snap-from", "snap-to");

            // R147: the primary constructor is located at its parameter list, so no edge
            // with the constructor as source or target may move when the type grows.
            var ctorId = from.ResolveId("global::N.Host.Host");
            var locationChanges = changes
                .Where(c => c.ChangeType == ChangeType.EdgeLocationChanged)
                .Where(c => c.DetailJson!.Contains($"\"source\":\"{ctorId}\"", StringComparison.Ordinal) ||
                            c.DetailJson!.Contains($"\"target\":\"{ctorId}\"", StringComparison.Ordinal))
                .ToList();
            Assert.True(locationChanges.Count == 0,
                "edge_location_changed for an edge of the primary constructor that did not move: " +
                string.Join("; ", locationChanges.Select(c => c.DetailJson)));
        }
        finally
        {
            TestDatabase.Delete(dbPath);
        }
    }

    [Fact]
    public async Task DeclaresEdge_PrimaryConstructorUsesItsParameterListLocation()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                class Foo { }
                                                class Bar { }
                                                class Host(Foo a, Bar b) { public void M() { } }
                                                """));

        var edge = extraction.SingleEdge("Declares", "global::N.Host", "global::N.Host.Host");
        Assert.Equal((3, 10, 3, 24),
            (edge.SourceStartLine, edge.SourceStartColumn, edge.SourceEndLine, edge.SourceEndColumn));
    }

    [Fact]
    public async Task SymbolDeclaration_PrimaryConstructorSpansItsParameterList()
    {
        const string source = """
                              namespace N;
                              class Foo { }
                              class Bar { }
                              class Host(Foo a, Bar b) { public void M() { } }
                              """;
        var extraction = await ExtractAsync(One(source));

        var ctor = Assert.Single(extraction.Result.Declarations,
            d => d.SymbolId.DocCommentId == "M:N.Host.#ctor(N.Foo,N.Bar)");
        var listStart = source.IndexOf("(Foo a, Bar b)", StringComparison.Ordinal);
        Assert.Equal(listStart, ctor.FullSpan.Start);
        // FullSpan includes the trailing trivia after the parameter list.
        Assert.Equal("(Foo a, Bar b)",
            source.Substring(ctor.FullSpan.Start!.Value, ctor.FullSpan.Length).TrimEnd());

        // R151: a constructor is named after its type, as ConstructorDeclarationSyntax.Identifier is.
        // The parameter list is both the declaration and the signature; there is no body.
        Assert.Equal(ctor.FullSpan.Start, ctor.SignatureSpan.Start);
        Assert.Equal(ctor.FullSpan.End, ctor.SignatureSpan.End);
        Assert.Null(ctor.BodySpan.Start);
        Assert.Equal("Host", source.Substring(ctor.NameSpan.Start!.Value, ctor.NameSpan.Length));

        // The type keeps its whole declaration span.
        var type = Assert.Single(extraction.Result.Declarations,
            d => d.SymbolId.DocCommentId == "T:N.Host");
        Assert.Equal(source.IndexOf("class Host", StringComparison.Ordinal), type.FullSpan.Start);
        Assert.Equal(source.Length, type.FullSpan.End);
    }

    [Fact]
    public async Task SymbolDeclaration_RecordPrimaryConstructorSpansItsParameterList()
    {
        const string source = """
                              namespace N;
                              public sealed record R(int A);
                              """;
        var extraction = await ExtractAsync(One(source));

        var ctor = Assert.Single(extraction.Result.Declarations,
            d => d.SymbolId.DocCommentId == "M:N.R.#ctor(System.Int32)");
        var listStart = source.IndexOf("(int A)", StringComparison.Ordinal);
        Assert.Equal(listStart, ctor.FullSpan.Start);
        Assert.Equal("(int A)",
            source.Substring(ctor.FullSpan.Start!.Value, ctor.FullSpan.Length).TrimEnd());
        Assert.Equal("R", source.Substring(ctor.NameSpan.Start!.Value, ctor.NameSpan.Length));
    }

    [Fact]
    public async Task NameOfChain_RecordsEveryNamedSymbolAsReflectionMemberRef()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Holder { public int Value { get; } }
                                                public class C
                                                {
                                                    private readonly Holder _holder = new();
                                                    public string N() => nameof(_holder.Value);
                                                }
                                                """));

        // R147: a nameof is a compile-time name for every symbol in its chain, not a read.
        var field = extraction.SingleEdge("ReflectionMemberRef", "global::N.C.N", "global::N.C._holder");
        Assert.Equal(Provenance.CompilerProved, field.Provenance);

        // The argument's own symbol keeps its edge.
        extraction.SingleEdge("ReflectionMemberRef", "global::N.C.N", "global::N.Holder.Value");

        Assert.DoesNotContain(extraction.Result.Edges,
            e => (e.Kind == "Reads" || e.Kind == "Writes") &&
                 (e.TargetSymbolId == extraction.ResolveId("global::N.C._holder") ||
                  e.TargetSymbolId == extraction.ResolveId("global::N.Holder.Value")));

        var rows = string.Join(", ", extraction.Result.BindingIncompleteness
            .Select(r => $"{r.DocumentPath}|{r.Reason}|{r.Count}"));
        Assert.True(extraction.Result.BindingIncompleteness.All(r => r.Reason == "filtered_external"),
            $"non-filtered binding-incompleteness row for the nameof document: {rows}");
    }

    [Fact]
    public async Task NameOfMethodGroup_IsNotAnUnresolvedRead()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class C
                                                {
                                                    public string? Name => nameof(N);
                                                    public void N() { }
                                                }
                                                """));

        // R142: a method group inside nameof has no symbol, only candidate symbols. The
        // Reads/Writes extractor recorded it as an unresolved read, which wrote one
        // unsupported_syntax row for the whole document.
        var rows = string.Join(", ", extraction.Result.BindingIncompleteness
            .Select(r => $"{r.DocumentPath}|{r.Reason}|{r.Count}"));
        Assert.True(extraction.Result.BindingIncompleteness.All(r => r.Reason != "unsupported_syntax"),
            $"unsupported_syntax row: {rows}");
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "Reads" && e.TargetSymbolId.Contains("C.N(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrimaryConstructorController_WithNameOfAction_RecordsNoIncompleteBinding()
    {
        // R142 probe: the shape of an eNoteV2 controller document that held one
        // unsupported_syntax row and so made its primary constructor an `unresolved`
        // dead candidate. The row must not appear.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public abstract class ControllerBase { }
                                                [System.AttributeUsage(System.AttributeTargets.Method)]
                                                public sealed class HttpGetAttribute : System.Attribute { }
                                                public interface IService { string Name(); }
                                                public sealed class C(IService s) : ControllerBase
                                                {
                                                    [HttpGet]
                                                    public string Get() => s.Name();
                                                    public string? Route() => nameof(GetContent);
                                                    [HttpGet("{id}")]
                                                    public string GetContent() => string.Empty;
                                                }
                                                """));

        var rows = string.Join(", ", extraction.Result.BindingIncompleteness
            .Select(r => $"{r.DocumentPath}|{r.Reason}|{r.Count}"));
        Assert.True(extraction.Result.BindingIncompleteness.All(r => r.Reason != "unsupported_syntax"),
            $"unsupported_syntax row for the controller document: {rows}");
    }
}