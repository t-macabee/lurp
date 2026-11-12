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
        string sourceDocumentPathEndsWith)
    {
        Assert.Equal(kind, edge.Kind);
        Assert.Equal(provenance, edge.Provenance);
        Assert.Equal(extractorVersion, edge.ExtractorVersion);
        Assert.NotNull(edge.SourceDocumentPath);
        Assert.EndsWith(sourceDocumentPathEndsWith, edge.SourceDocumentPath);
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
        AssertEdgeContract(edge, "Calls", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("Calls", ("global::N.Calculator.Compute", "global::N.Calculator.Add"));
    }

    [Fact]
    public async Task CallsEdge_RecursiveCall_IsAnEdgeFromTheMemberToItself()
    {
        // D13: the call exists in the source, so a recursive call is a Calls fact from the member
        // to itself; a recursive call of a virtual member is also a StaticallyCalls fact.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class R
                                                {
                                                    public static int Fact(int k) => k <= 1 ? 1 : k * Fact(k - 1);
                                                    public virtual int Depth(int n) => n <= 0 ? 0 : Depth(n - 1);
                                                }
                                                """));

        extraction.AssertEdgeSet("Calls", ("global::N.R.Fact", "global::N.R.Fact"), ("global::N.R.Depth", "global::N.R.Depth"));
        extraction.AssertEdgeSet("StaticallyCalls", ("global::N.R.Depth", "global::N.R.Depth"));
    }

    [Fact]
    public async Task ReferencesEdge_StaticReceiverOfEveryMemberKind()
    {
        // D124: a static member access names its receiver type, whatever the member kind, with
        // the receiver's type arguments. An unqualified access and a nameof argument name none.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Arg { }
                                                public static class S
                                                {
                                                    public static int Field;
                                                    public static int Prop { get; set; }
                                                    public static event System.Action? Ev;
                                                    public static void M() { }
                                                    public const int K = 1;
                                                }
                                                public static class G<T> { public static int Value; }
                                                public class Use
                                                {
                                                    public int ReadField() => S.Field;
                                                    public int ReadProp() => S.Prop;
                                                    public void Subscribe() => S.Ev += Handler;
                                                    public System.Action Group() => S.M;
                                                    public int Constant() => S.K;
                                                    public int Generic() => G<Arg>.Value;
                                                    public string Name() => nameof(S.Field);
                                                    private static void Handler() { }
                                                }
                                                public static class Own
                                                {
                                                    public static int Value;
                                                    public static int Unqualified() => Value;
                                                }
                                                """));

        extraction.AssertEdgeSet("References",
            ("global::N.Use.ReadField", "global::N.S"),
            ("global::N.Use.ReadProp", "global::N.S"),
            ("global::N.Use.Subscribe", "global::N.S"),
            ("global::N.Use.Group", "global::N.S"),
            ("global::N.Use.Constant", "global::N.S"),
            ("global::N.Use.Generic", "global::N.G<T>"),
            ("global::N.Use.Generic", "global::N.Arg"));
    }

    [Fact]
    public async Task ReferencesEdge_BaseListTypeArgumentsAndWrittenConstraints()
    {
        // D14: a type argument in a base list and each written constraint type are References
        // from the declaring type (a method constraint: from the method). A record's compiler-made
        // IEquatable<R> and the inherited constraint of an override are not written, so Rec has no
        // References to itself. D38, D126: the signatures of the record's compiler-made members
        // (Equals(Rec), == and !=, the clone method) name Rec.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class A { }
                                                public class B<T> { }
                                                public interface I<T> { }
                                                public class X { }
                                                public class Y { }
                                                public class Z { }
                                                public class D : B<A>, I<X> { }
                                                public class Con<T> where T : Y { }
                                                public class Methods { public virtual void M<T>() where T : Z { } }
                                                public class Derived : Methods { public override void M<T>() { } }
                                                public record Rec(int V);
                                                public delegate void Del<T>() where T : A;
                                                """));

        extraction.AssertEdgeSet("References",
            ("global::N.D", "global::N.A"),
            ("global::N.D", "global::N.X"),
            ("global::N.Con<T>", "global::N.Y"),
            ("global::N.Methods.M<T>", "global::N.Z"),
            ("global::N.Del<T>", "global::N.A"),
            ("global::N.Rec.<Clone>$", "global::N.Rec"),
            ("global::N.Rec.Equals", "global::N.Rec"),
            ("global::N.Rec.operator ==", "global::N.Rec"),
            ("global::N.Rec.operator !=", "global::N.Rec"));
    }

    [Fact]
    public async Task CallsEdge_CollectionExpressionAddsEachElementAndEnumeratesASpread()
    {
        // C# collection expressions: a type with Add is built by its constructor and one Add call
        // per element; a spread element is enumerated like foreach (S01-F10).
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Bag { public Enumer GetEnumerator() => new(); }
                                                public class Enumer { public int Current => 1; public bool MoveNext() => false; }
                                                public class Box : System.Collections.IEnumerable
                                                {
                                                    public void Add(int v) { }
                                                    public System.Collections.IEnumerator GetEnumerator() => null!;
                                                }
                                                public static class U { public static Box Fill(Bag bag) => [0, .. bag]; }
                                                """));

        extraction.AssertEdgeSet("Calls",
            ("global::N.U.Fill", "global::N.Box.Add"),
            ("global::N.U.Fill", "global::N.Bag.GetEnumerator"),
            ("global::N.U.Fill", "global::N.Enumer.MoveNext"));
        extraction.AssertEdgeSet("Reads", ("global::N.U.Fill", "global::N.Enumer.Current"));
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
        AssertEdgeContract(edge, "Constructs", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("Constructs", ("global::N.Factory.Create", "global::N.Widget"));
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
        extraction.AssertEdgeSet("Inherits", ("global::N.Derived", "global::N.Base"));
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
        extraction.AssertEdgeSet("Implements", ("global::N.Service", "global::N.IService"));
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
        AssertEdgeContract(edge, "Overrides", Provenance.CompilerProved, "overrides-v2", Doc);
        extraction.AssertEdgeSet("Overrides", ("global::N.Derived.Do", "global::N.Base.Do"));
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
        AssertEdgeContract(edge, "Hides", Provenance.CompilerProved, "hides-v2", Doc);
        extraction.AssertEdgeSet("Hides", ("global::N.Derived.Do", "global::N.Base.Do"));
    }

    [Fact]
    public async Task OverridesEdge_EventOverridesTheInheritedVirtualEvent()
    {
        // S01-F17: C# 15.8.5, an override event overrides the inherited virtual event, also when
        // the direct base type does not declare it.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class A { public virtual event System.Action? Ev; }
                                                public class B : A { }
                                                public class C : B { public override event System.Action? Ev; }
                                                """));

        var edge = extraction.SingleEdge("Overrides", "global::N.C.Ev", "global::N.A.Ev");
        AssertEdgeContract(edge, "Overrides", Provenance.CompilerProved, "overrides-v2", Doc);
        extraction.AssertEdgeSet("Overrides", ("global::N.C.Ev", "global::N.A.Ev"));
    }

    [Fact]
    public async Task HidesEdge_FollowsTheCSharpHidingRules()
    {
        // S01-F17, C# 7.7.2.3 (hiding through inheritance) and 7.6 (signatures): a member hides the
        // inherited members with its name, also through a base type that does not declare them. A
        // method hides a base method with the same signature (number of type parameters, parameter
        // types and by-value or by-reference passing; method type parameters match by position)
        // and every base member that is not a method. A field, an event and a nested type hide every
        // base member with their name. A private base member is not inherited. B hides A.Gen, so C
        // inherits B.Gen and hides it, not A.Gen.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class A
                                                {
                                                    public void Do() { }
                                                    public void Sig(int x) { }
                                                    public void Sig(ref int x) { }
                                                    public void Gen() { }
                                                    public void Gen<T>() { }
                                                    public void G<T>(T x) { }
                                                    public int F;
                                                    public event System.Action? E;
                                                    public class Nested { }
                                                    public int Shared() => 0;
                                                    public int Prop { get; set; }
                                                    private void Own() { }
                                                }
                                                public class B : A { public new void Gen() { } }
                                                public class C : B
                                                {
                                                    public new void Do() { }
                                                    public new void Sig(int x) { }
                                                    public new void Gen() { }
                                                    public new void G<U>(U x) { }
                                                    public new int F;
                                                    public new event System.Action? E;
                                                    public new class Nested { }
                                                    public new int Shared;
                                                    public new void Prop() { }
                                                    public void Own() { }
                                                }
                                                """));

        extraction.AssertDocIdEdgeSet("Hides",
            ("M:N.C.Do", "M:N.A.Do"),
            ("M:N.C.Sig(System.Int32)", "M:N.A.Sig(System.Int32)"),
            ("M:N.B.Gen", "M:N.A.Gen"),
            ("M:N.C.Gen", "M:N.B.Gen"),
            ("M:N.C.G``1(``0)", "M:N.A.G``1(``0)"),
            ("F:N.C.F", "F:N.A.F"),
            ("E:N.C.E", "E:N.A.E"),
            ("T:N.C.Nested", "T:N.A.Nested"),
            ("F:N.C.Shared", "M:N.A.Shared"),
            ("M:N.C.Prop", "P:N.A.Prop"));
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
        AssertEdgeContract(edge, "Reads", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("Reads", ("global::N.Counter.GetValue", "global::N.Counter._value"));
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
        AssertEdgeContract(edge, "Writes", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("Writes", ("global::N.Counter.SetValue", "global::N.Counter._value"));
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

        extraction.AssertEdgeSet("Writes",
            ("global::N.User.Set", "global::N.Target.P"),
            ("global::N.User.Set", "global::N.User._g"),
            ("global::N.User.Set", "global::N.Target.F"),
            ("global::N.User.SetConditional", "global::N.Target.P"));
        extraction.AssertEdgeSet("Reads");
    }

    [Fact]
    public async Task ReadsWrites_EachAccessShape_FollowsD11()
    {
        // D11: '=', an out argument and a tuple target of a deconstruction write. A compound
        // assignment, ??=, ++, a ref argument and a writable ref alias (a ref local, a ref
        // return) read and write. An in argument and a ref readonly alias only read.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class C
                                                {
                                                    public int Compound; public string? Coalesced; public int Incremented;
                                                    public int TupleLeft; public int TupleRight; public int ByRef; public int ByOut;
                                                    public int ByIn; public int Aliased; public int ReadOnlyAliased; public int Returned;
                                                    public int Total { get; set; }
                                                }
                                                public static class U
                                                {
                                                    public static void Compound(C c) => c.Compound += 1;
                                                    public static void CompoundProperty(C c) => c.Total += 2;
                                                    public static void Coalesce(C c) => c.Coalesced ??= "x";
                                                    public static void Increment(C c) => c.Incremented++;
                                                    public static void Tuple(C c) => (c.TupleLeft, c.TupleRight) = (1, 2);
                                                    public static void RefArgument(C c) => Bump(ref c.ByRef);
                                                    public static void OutArgument(C c) => Set(out c.ByOut);
                                                    public static int InArgument(C c) => Peek(in c.ByIn);
                                                    public static void RefLocal(C c) { ref int alias = ref c.Aliased; alias = 5; }
                                                    public static int RefReadOnlyLocal(C c) { ref readonly int alias = ref c.ReadOnlyAliased; return alias; }
                                                    public static ref int RefReturn(C c) => ref c.Returned;
                                                    private static void Bump(ref int value) => value++;
                                                    private static void Set(out int value) => value = 1;
                                                    private static int Peek(in int value) => value;
                                                }
                                                """));

        extraction.AssertEdgeSet("Reads",
            ("global::N.U.Compound", "global::N.C.Compound"),
            ("global::N.U.CompoundProperty", "global::N.C.Total"),
            ("global::N.U.Coalesce", "global::N.C.Coalesced"),
            ("global::N.U.Increment", "global::N.C.Incremented"),
            ("global::N.U.RefArgument", "global::N.C.ByRef"),
            ("global::N.U.InArgument", "global::N.C.ByIn"),
            ("global::N.U.RefLocal", "global::N.C.Aliased"),
            ("global::N.U.RefReadOnlyLocal", "global::N.C.ReadOnlyAliased"),
            ("global::N.U.RefReturn", "global::N.C.Returned"));
        extraction.AssertEdgeSet("Writes",
            ("global::N.U.Compound", "global::N.C.Compound"),
            ("global::N.U.CompoundProperty", "global::N.C.Total"),
            ("global::N.U.Coalesce", "global::N.C.Coalesced"),
            ("global::N.U.Increment", "global::N.C.Incremented"),
            ("global::N.U.Tuple", "global::N.C.TupleLeft"),
            ("global::N.U.Tuple", "global::N.C.TupleRight"),
            ("global::N.U.RefArgument", "global::N.C.ByRef"),
            ("global::N.U.OutArgument", "global::N.C.ByOut"),
            ("global::N.U.RefLocal", "global::N.C.Aliased"),
            ("global::N.U.RefReturn", "global::N.C.Returned"));
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
        AssertEdgeContract(edge, "Returns", Provenance.CompilerProved, "returns-v2", Doc);
        extraction.AssertEdgeSet("Returns", ("global::N.Producer.Produce", "global::N.Result"));
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
        AssertEdgeContract(edge, "Throws", Provenance.CompilerProved, "operation-shapes-v2", Doc);
    }

    [Fact]
    public async Task ThrowsEdge_EveryRoot_ThrowsFromTheMemberThatRunsIt()
    {
        // S01-F23: a throw in a field or property initializer, an event accessor, a lambda or a
        // local function is a Throws fact of the member that runs the code (golden mapping
        // rule 5). A rethrow ('throw;') names no type.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class E1 : System.Exception { }
                                                public class E2 : System.Exception { }
                                                public class E3 : System.Exception { }
                                                public class E4 : System.Exception { }
                                                public class E5 : System.Exception { }
                                                public class Host
                                                {
                                                    public object? Field = (object?)null ?? throw new E1();
                                                    public object Prop { get; } = (object?)null ?? throw new E2();
                                                    public event System.Action? Ev { add { throw new E3(); } remove { } }
                                                    public void M()
                                                    {
                                                        System.Action a = () => throw new E4();
                                                        try { Local(); } catch (E5) { throw; }
                                                        static void Local() => throw new E5();
                                                    }
                                                }
                                                """));

        extraction.AssertEdgeSet("Throws",
            ("global::N.Host.Field", "global::N.E1"),
            ("global::N.Host.Prop", "global::N.E2"),
            ("global::N.Host.Ev.add", "global::N.E3"),
            ("global::N.Host.M", "global::N.E4"),
            ("global::N.Host.M", "global::N.E5"));
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
        AssertEdgeContract(method, "Declares", Provenance.CompilerProved, "declares-v2", Doc);

        var field = extraction.SingleEdge("Declares", "global::N.Container", "global::N.Container._field");
        AssertEdgeContract(field, "Declares", Provenance.CompilerProved, "declares-v2", Doc);

        // D38: the implicit constructor has no declaring syntax, so Lurp does not store it.
        extraction.AssertEdgeSet("Declares",
            ("global::N.Container", "global::N.Container.Method"),
            ("global::N.Container", "global::N.Container._field"));
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
        AssertEdgeContract(edge, "References", Provenance.CompilerProved, "parameter-deps-v2", Doc);
        extraction.AssertEdgeSet("References", ("global::N.Service.Service", "global::N.Logger"));
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
        AssertEdgeContract(edge, "References", Provenance.CompilerProved, "parameter-deps-v2", Doc);

        var invokeId = extraction.ResolveId("global::N.Handler.Invoke");
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "References" && e.SourceSymbolId == invokeId);
    }

    [Fact]
    public async Task CompilerMadeRecordMembers_HaveTheStructuralFactsOfWrittenMembers()
    {
        // D38, D126: a compiler-made member that Lurp stores has the structural facts of a written
        // member. The C# rules for records synthesize Equals(R), Equals(object), GetHashCode,
        // ToString, PrintMembers, the == and != operators, the clone method and Deconstruct, and the
        // accessors of each positional property; in a derived record, Equals(Base), Equals(object),
        // GetHashCode, ToString, PrintMembers and the clone method override the base record's members,
        // and the clone method returns the derived record type. EqualityContract and the copy
        // constructor have no declaring syntax, so Lurp does not store them and they have no fact.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public record RBase(int A);
                                                public record RDer(int A, int B) : RBase(A);
                                                """));

        extraction.AssertDocIdEdgeSet("Declares",
            ("T:N.RBase", "M:N.RBase.#ctor(System.Int32)"),
            ("T:N.RBase", "P:N.RBase.A"),
            ("T:N.RBase", "M:N.RBase.get_A"),
            ("T:N.RBase", "M:N.RBase.set_A(System.Int32)"),
            ("T:N.RBase", "M:N.RBase.{Clone}$"),
            ("T:N.RBase", "M:N.RBase.Deconstruct(System.Int32@)"),
            ("T:N.RBase", "M:N.RBase.Equals(System.Object)"),
            ("T:N.RBase", "M:N.RBase.Equals(N.RBase)"),
            ("T:N.RBase", "M:N.RBase.GetHashCode"),
            ("T:N.RBase", "M:N.RBase.ToString"),
            ("T:N.RBase", "M:N.RBase.PrintMembers(System.Text.StringBuilder)"),
            ("T:N.RBase", "M:N.RBase.op_Equality(N.RBase,N.RBase)"),
            ("T:N.RBase", "M:N.RBase.op_Inequality(N.RBase,N.RBase)"),
            ("T:N.RDer", "M:N.RDer.#ctor(System.Int32,System.Int32)"),
            ("T:N.RDer", "P:N.RDer.B"),
            ("T:N.RDer", "M:N.RDer.get_B"),
            ("T:N.RDer", "M:N.RDer.set_B(System.Int32)"),
            ("T:N.RDer", "M:N.RDer.{Clone}$"),
            ("T:N.RDer", "M:N.RDer.Deconstruct(System.Int32@,System.Int32@)"),
            ("T:N.RDer", "M:N.RDer.Equals(System.Object)"),
            ("T:N.RDer", "M:N.RDer.Equals(N.RBase)"),
            ("T:N.RDer", "M:N.RDer.Equals(N.RDer)"),
            ("T:N.RDer", "M:N.RDer.GetHashCode"),
            ("T:N.RDer", "M:N.RDer.ToString"),
            ("T:N.RDer", "M:N.RDer.PrintMembers(System.Text.StringBuilder)"),
            ("T:N.RDer", "M:N.RDer.op_Equality(N.RDer,N.RDer)"),
            ("T:N.RDer", "M:N.RDer.op_Inequality(N.RDer,N.RDer)"));
        extraction.AssertDocIdEdgeSet("Overrides",
            ("M:N.RDer.Equals(N.RBase)", "M:N.RBase.Equals(N.RBase)"),
            ("M:N.RDer.Equals(System.Object)", "M:N.RBase.Equals(System.Object)"),
            ("M:N.RDer.GetHashCode", "M:N.RBase.GetHashCode"),
            ("M:N.RDer.ToString", "M:N.RBase.ToString"),
            ("M:N.RDer.PrintMembers(System.Text.StringBuilder)", "M:N.RBase.PrintMembers(System.Text.StringBuilder)"),
            ("M:N.RDer.{Clone}$", "M:N.RBase.{Clone}$"));
        extraction.AssertDocIdEdgeSet("Returns",
            ("M:N.RBase.{Clone}$", "T:N.RBase"),
            ("M:N.RDer.{Clone}$", "T:N.RDer"));
        extraction.AssertDocIdEdgeSet("References",
            ("M:N.RBase.{Clone}$", "T:N.RBase"),
            ("M:N.RBase.Equals(N.RBase)", "T:N.RBase"),
            ("M:N.RBase.op_Equality(N.RBase,N.RBase)", "T:N.RBase"),
            ("M:N.RBase.op_Inequality(N.RBase,N.RBase)", "T:N.RBase"),
            ("M:N.RDer.{Clone}$", "T:N.RDer"),
            ("M:N.RDer.Equals(N.RBase)", "T:N.RBase"),
            ("M:N.RDer.Equals(N.RDer)", "T:N.RDer"),
            ("M:N.RDer.op_Equality(N.RDer,N.RDer)", "T:N.RDer"),
            ("M:N.RDer.op_Inequality(N.RDer,N.RDer)", "T:N.RDer"));

        // D38: the location of a member without syntax of its own is the declaration of its type.
        var clone = extraction.Result.Edges.Single(e => e.Kind == "Overrides" && e.SourceSymbolId.StartsWith("M:N.RDer.{Clone}$|", StringComparison.Ordinal));
        AssertEdgeContract(clone, "Overrides", Provenance.CompilerProved, "overrides-v2", Doc);
        Assert.Equal((2, 0), (clone.SourceStartLine, clone.SourceStartColumn));
    }

    [Fact]
    public async Task DelegateType_OwnsItsSignatureAndDeclaresItsMembers()
    {
        // D23, D38: the delegate type owns the signature of its Invoke (Returns and References), and
        // declares its four members (C# delegates: a constructor, Invoke, BeginInvoke and EndInvoke).
        // The other members own no signature fact: their source types are the Invoke types.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Payload { }
                                                public class Result { }
                                                public delegate Result Handler(Payload p);
                                                """));

        extraction.AssertDocIdEdgeSet("Declares",
            ("T:N.Handler", "M:N.Handler.#ctor(System.Object,System.IntPtr)"),
            ("T:N.Handler", "M:N.Handler.Invoke(N.Payload)"),
            ("T:N.Handler", "M:N.Handler.BeginInvoke(N.Payload,System.AsyncCallback,System.Object)"),
            ("T:N.Handler", "M:N.Handler.EndInvoke(System.IAsyncResult)"));
        extraction.AssertDocIdEdgeSet("Returns", ("T:N.Handler", "T:N.Result"));
        extraction.AssertDocIdEdgeSet("References", ("T:N.Handler", "T:N.Payload"), ("T:N.Handler", "T:N.Result"));

        var returns = extraction.SingleEdge("Returns", "global::N.Handler", "global::N.Result");
        AssertEdgeContract(returns, "Returns", Provenance.CompilerProved, "returns-v2", Doc);
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
        AssertEdgeContract(fromParameter, "References", Provenance.CompilerProved, "operation-shapes-v2", Doc);

        var fromReturn = extraction.SingleEdge("References", "global::N.Host.M", "global::N.R");
        AssertEdgeContract(fromReturn, "References", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("References", ("global::N.Host.M", "global::N.P"), ("global::N.Host.M", "global::N.R"));

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
            AssertEdgeContract(edge, "References", Provenance.CompilerProved, "parameter-deps-v2", Doc);
        }

        extraction.AssertEdgeSet("References",
            ("global::N.PExtensions.Size", "global::N.P"),
            ("global::N.PExtensions.get_Length", "global::N.P"),
            ("global::N.PExtensions.Make", "global::N.P"));
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
        AssertEdgeContract(fromParameter, "References", Provenance.CompilerProved, "operation-shapes-v2", Doc);

        var fromReturn = extraction.SingleEdge("References", "global::N.Host.M", "global::N.R");
        AssertEdgeContract(fromReturn, "References", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("References", ("global::N.Host.M", "global::N.P"), ("global::N.Host.M", "global::N.R"));
    }

    [Fact]
    public async Task OperationShapesEdge_InferredLambdaTypesReferenceTypesFromEnclosingMember()
    {
        // D12: facts do not change under a syntax-only rewrite, so an implicitly typed lambda
        // parameter ('q => 1' for '(Q q) => 1') references its type from the enclosing member.
        // D136: the same holds for an inferred lambda return type ('() => new R()').
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Q { }
                                                public class R { }
                                                public static class Host
                                                {
                                                    public static void Run(System.Func<Q, int> f) { }
                                                    public static void Make(System.Func<R> f) { }
                                                    public static void M() { Run(q => 1); }
                                                    public static void N() { Make(() => new R()); }
                                                }
                                                """));

        extraction.AssertEdgeSet("References",
            ("global::N.Host.Run", "global::N.Q"),
            ("global::N.Host.Make", "global::N.R"),
            ("global::N.Host.M", "global::N.Q"),
            ("global::N.Host.N", "global::N.R"));
    }

    [Fact]
    public async Task ExtensionReceiverEdge_CollectionInitializerExtensionAdd()
    {
        // C# collection initializer: each element calls the accessible Add of the target
        // object; an extension Add binds with the collection as its receiver.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Bag : System.Collections.IEnumerable
                                                {
                                                    public System.Collections.IEnumerator GetEnumerator() => throw new System.NotSupportedException();
                                                }
                                                public static class BagExtensions
                                                {
                                                    public static void Add(this Bag bag, int item) { }
                                                }
                                                public class Filler
                                                {
                                                    public Bag Make() => new Bag { 1, 2 };
                                                }
                                                """));

        extraction.AssertEdgeSet("ExtensionReceiver", ("global::N.Bag", "global::N.BagExtensions.Add"));
        extraction.AssertEdgeSet("Calls", ("global::N.Filler.Make", "global::N.BagExtensions.Add"));
    }

    [Fact]
    public async Task CallsEdge_TwoReceiverTypes_KeepsTheConstraintOfEachSite()
    {
        // Guard for the S01-F22 move: the receiver-type constraints of all sites of one
        // Calls relation are kept (union), as the syntax extractor kept them.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public interface IShape { void Draw(); }
                                                public interface IRound : IShape { }
                                                public interface ISquare : IShape { }
                                                public class Painter
                                                {
                                                    public void Paint(IRound round, ISquare square) { round.Draw(); square.Draw(); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("Calls", "global::N.Painter.Paint", "global::N.IShape.Draw");
        var expected = new[] { extraction.ResolveId("global::N.IRound"), extraction.ResolveId("global::N.ISquare") }
            .Order(StringComparer.Ordinal)
            .Select(static id => new[] { id });
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), edge.ReceiverTypeConstraintsJson);
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
        AssertEdgeContract(receiver, "ExtensionReceiver", Provenance.CompilerProved, "operation-shapes-v2", Doc);

        var call = extraction.SingleEdge("Calls", "global::N.Consumer.Use", "global::N.TargetExtensions.Extend");
        AssertEdgeContract(call, "Calls", Provenance.CompilerProved, "operation-shapes-v2", Doc);
        extraction.AssertEdgeSet("ExtensionReceiver", ("global::N.Target", "global::N.TargetExtensions.Extend"));
        extraction.AssertEdgeSet("Calls", ("global::N.Consumer.Use", "global::N.TargetExtensions.Extend"));
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
        AssertEdgeContract(edge, "MayDispatchTo", Provenance.CompilerProved, "polymorphism-v2", Doc);
        extraction.AssertEdgeSet("MayDispatchTo", ("global::N.IService.Execute", "global::N.Service.Execute"));
    }

    [Fact]
    public async Task MayDispatchToEdge_EveryAncestorReachesEveryDescendantOverride()
    {
        // D15: a call on the static type V1 can run V2.Do and V3.Do, so MayDispatchTo goes from
        // every ancestor in an override chain to every descendant override (the sealed V3.Do is a
        // descendant too). C#: the accessors of an overriding property override the base accessors;
        // a field-like event's accessors have no syntax, so Lurp does not store them.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class V0 { public virtual void Do() { } public virtual int P => 0; public virtual event System.Action? Ev; }
                                                public class V1 : V0 { public override void Do() { } public override int P => 1; }
                                                public class V2 : V1 { public override void Do() { } public override int P => 2; public override event System.Action? Ev; }
                                                public sealed class V3 : V2 { public override void Do() { } }
                                                """));

        extraction.AssertEdgeSet("MayDispatchTo",
            ("global::N.V0.Do", "global::N.V1.Do"),
            ("global::N.V0.Do", "global::N.V2.Do"),
            ("global::N.V0.Do", "global::N.V3.Do"),
            ("global::N.V1.Do", "global::N.V2.Do"),
            ("global::N.V1.Do", "global::N.V3.Do"),
            ("global::N.V2.Do", "global::N.V3.Do"),
            ("global::N.V0.P", "global::N.V1.P"),
            ("global::N.V0.P", "global::N.V2.P"),
            ("global::N.V1.P", "global::N.V2.P"),
            ("global::N.V0.P.get", "global::N.V1.P.get"),
            ("global::N.V0.P.get", "global::N.V2.P.get"),
            ("global::N.V1.P.get", "global::N.V2.P.get"),
            ("global::N.V0.Ev", "global::N.V2.Ev"));
    }

    [Fact]
    public async Task MayDispatchToEdge_BaseRecordMembersReachTheDerivedRecordOverrides()
    {
        // D38, D15: in a derived record, Equals(Base), Equals(object), GetHashCode, ToString,
        // PrintMembers and the clone method override the base record's members (C# records), so a
        // call on the base record can run them.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public record RBase(int A);
                                                public record RDer(int A, int B) : RBase(A);
                                                """));

        extraction.AssertDocIdEdgeSet("MayDispatchTo",
            ("M:N.RBase.Equals(N.RBase)", "M:N.RDer.Equals(N.RBase)"),
            ("M:N.RBase.Equals(System.Object)", "M:N.RDer.Equals(System.Object)"),
            ("M:N.RBase.GetHashCode", "M:N.RDer.GetHashCode"),
            ("M:N.RBase.ToString", "M:N.RDer.ToString"),
            ("M:N.RBase.PrintMembers(System.Text.StringBuilder)", "M:N.RDer.PrintMembers(System.Text.StringBuilder)"),
            ("M:N.RBase.{Clone}$", "M:N.RDer.{Clone}$"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MayDispatchToEdge_ProvenanceDoesNotDependOnTypeOrder(bool derivedFirst)
    {
        // S01-F15: Base declares Run and implements IFoo with it, so (IFoo.Run, Base.Run) is
        // compiler_proved; the derived type that inherits it does not weaken the pair. Plain.Run
        // implements IFoo only through Late, a derived type, so (IFoo.Run, Plain.Run) is possible
        // (class comment of InterfaceDispatchExtractor, slice 1 claim S01-C8).
        const string derived = "public class Derived : Base { }";
        const string @base = "public class Base : IFoo { public void Run() { } }";
        var extraction = await ExtractAsync(One($$"""
                                                  namespace N;
                                                  public interface IFoo { void Run(); }
                                                  {{(derivedFirst ? derived : @base)}}
                                                  {{(derivedFirst ? @base : derived)}}
                                                  public class Plain { public void Run() { } }
                                                  public class Late : Plain, IFoo { }
                                                  """));

        extraction.AssertEdgeSet("MayDispatchTo", ("global::N.IFoo.Run", "global::N.Base.Run"));
        var possible = Assert.Single(extraction.EdgesOf("MayDispatchTo", Provenance.Possible));
        Assert.Equal(extraction.ResolveId("global::N.IFoo.Run"), possible.SourceSymbolId);
        Assert.Equal(extraction.ResolveId("global::N.Plain.Run"), possible.TargetSymbolId);
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
        AssertEdgeContract(edge, "StaticallyCalls", Provenance.CompilerProved, "operation-shapes-v2", Doc);

        extraction.AssertEdgeSet("StaticallyCalls", ("global::N.Handler.Handle", "global::N.IRepo.Save"));

        // The same call site also carries a plain Calls edge to the interface method.
        extraction.AssertEdgeSet("Calls", ("global::N.Handler.Handle", "global::N.IRepo.Save"));
    }

    [Fact]
    public async Task StaticallyCallsEdge_EveryAccessThatCanDispatch()
    {
        // D22, D15: an access whose target can dispatch at run time is a StaticallyCalls fact: a
        // virtual, abstract or overriding member (an interface member included) that is not sealed and
        // whose type is not sealed. That covers invocations, property and indexer accesses, an event
        // += or -=, and the implicit calls and reads of foreach. A base. access calls the base member
        // without dispatch (C# 12.8.15). A raise of a field-like event inside its class reads the
        // event's field and runs no accessor. A member of a sealed class (S.Do) and a delegate's Invoke
        // cannot dispatch. A method group is not a call. A static abstract interface member called
        // through a type parameter dispatches on the type argument.
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public interface IRepo { void Save(); int Count { get; } }
                                                public interface IFactory<T> where T : IFactory<T> { static abstract int Make(); }
                                                public class V0
                                                {
                                                    public virtual void Do() { }
                                                    public virtual int P { get; set; }
                                                    public virtual int this[int i] => i;
                                                    public virtual event System.Action? Ev;
                                                    public void Plain() { }
                                                    public void Raise() => Ev?.Invoke();
                                                }
                                                public class V1 : V0
                                                {
                                                    public override void Do() { base.Do(); }
                                                    public override int P { get => base.P; set { } }
                                                }
                                                public sealed class S : V1 { public override void Do() { } }
                                                public delegate void Del();
                                                public class Seq { public virtual Cursor GetEnumerator() => new(); }
                                                public class Cursor { public virtual bool MoveNext() => false; public virtual int Current => 0; }
                                                public class Use
                                                {
                                                    public void Invoke(V1 v, S s, IRepo r, Del d) { v.Do(); s.Do(); r.Save(); v.Plain(); d(); }
                                                    public int Access(V0 v, IRepo r) { v.P = 1; return v.P + v[0] + r.Count; }
                                                    public void Subscribe(V0 v) { v.Ev += Handler; }
                                                    public System.Action Group(V0 v) => v.Do;
                                                    public void Loop(Seq seq) { foreach (var x in seq) { } }
                                                    public int Generic<T>() where T : IFactory<T> => T.Make();
                                                    private void Handler() { }
                                                }
                                                """));

        extraction.AssertEdgeSet("StaticallyCalls",
            ("global::N.Use.Invoke", "global::N.V1.Do"),
            ("global::N.Use.Invoke", "global::N.IRepo.Save"),
            ("global::N.Use.Access", "global::N.V0.P"),
            ("global::N.Use.Access", "global::N.V0.this"),
            ("global::N.Use.Access", "global::N.IRepo.Count"),
            ("global::N.Use.Subscribe", "global::N.V0.Ev"),
            ("global::N.Use.Loop", "global::N.Seq.GetEnumerator"),
            ("global::N.Use.Loop", "global::N.Cursor.MoveNext"),
            ("global::N.Use.Loop", "global::N.Cursor.Current"),
            ("global::N.Use.Generic<T>", "global::N.IFactory<T>.Make"));
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
        extraction.AssertEdgeSet("Contains", ("global::N.Outer", "global::N.Outer.Inner"));
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

        extraction.AssertEdgeSet("Implements", ("global::N.Calculator", "global::N.ICalculator"));
        extraction.AssertEdgeSet("Reads",
            ("global::N.Calculator.Total.get", "global::N.Calculator._total"),
            ("global::N.Calculator.Add", "global::N.Calculator._total"));
        extraction.AssertEdgeSet("Writes", ("global::N.Calculator.Add", "global::N.Calculator._total"));
        extraction.AssertEdgeSet("Constructs", ("global::N.Calculator.Create", "global::N.Calculator"));
        extraction.AssertEdgeSet("Returns", ("global::N.Calculator.Create", "global::N.Calculator"));
        extraction.AssertEdgeSet("Calls");
        extraction.AssertEdgeSet("Declares",
            ("global::N.Calculator", "global::N.Calculator._total"),
            ("global::N.Calculator", "global::N.Calculator.Total"),
            ("global::N.Calculator", "global::N.Calculator.Total.get"),
            ("global::N.Calculator", "global::N.Calculator.Add"),
            ("global::N.Calculator", "global::N.Calculator.Create"),
            ("global::N.Calculator", "global::N.Calculator.Validate"),
            ("global::N.ICalculator", "global::N.ICalculator.Add"));

        // The only Calls edge targets the external ArgumentNullException constructor.
        var calls = Assert.Single(extraction.EdgesOf("Calls"));
        Assert.Equal(extraction.ResolveId("global::N.Calculator.Validate"), calls.SourceSymbolId);
        Assert.StartsWith("M:System.ArgumentNullException.#ctor", calls.TargetSymbolId);
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
        extraction.AssertEdgeSet("Calls", ("global::N.C.Get", "global::N.IService.Name"));
        extraction.AssertEdgeSet("ReflectionMemberRef", ("global::N.C.Route", "global::N.C.GetContent"));
    }
}