using Lurp.Storage;

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
        AssertEdgeContract(edge, "Inherits", Provenance.CompilerProved, "1.6.0", Doc);
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
        AssertEdgeContract(edge, "Implements", Provenance.CompilerProved, "1.6.0", Doc);
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
        AssertEdgeContract(edge, "Contains", Provenance.CompilerProved, "1.6.0", Doc);
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
}