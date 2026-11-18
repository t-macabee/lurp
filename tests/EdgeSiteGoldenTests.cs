using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     S01-F33 item 1 / D155.6: asserts the exact 0-based source span (line, column, end line,
///     end column) of every workspace edge kind. Each expected span is derived by hand from the
///     source text and checked through <see cref="GoldenEdgeTests.AssertEdgeContract" />.
/// </summary>
public sealed class EdgeSiteGoldenTests : InMemoryTestBase
{
    private const string Doc = "Source.cs";

    private static Dictionary<string, string> One(string source) => new() { [Doc] = source };

    [Fact]
    public async Task Calls_SiteIsTheInvocationExpression()
    {
        // Line 4: "    public void Run() { Target(); }"
        // "    public void Run() { " = 24 chars -> "Target()" starts at col 24, length 8 -> col 32.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class C
            {
                public void Target() { }
                public void Run() { Target(); }
            }
            """));

        var edge = extraction.SingleEdge("Calls", "global::N.C.Run", "global::N.C.Target");
        GoldenEdgeTests.AssertEdgeContract(edge, "Calls", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 24, 4, 32));
    }

    [Fact]
    public async Task Constructs_SiteIsTheObjectCreationExpression()
    {
        // Line 4: "    public Item Make() => new Item();"
        // "    public Item Make() => " = 26 chars -> "new Item()" starts at col 26, length 10 -> col 36.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Item { }
            public class Factory
            {
                public Item Make() => new Item();
            }
            """));

        var edge = extraction.SingleEdge("Constructs", "global::N.Factory.Make", "global::N.Item");
        GoldenEdgeTests.AssertEdgeContract(edge, "Constructs", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 26, 4, 36));
    }

    [Fact]
    public async Task Reads_SiteIsTheMemberReferenceExpression()
    {
        // Line 4: "    public int Read() => _value;"
        // "    public int Read() => " = 25 chars -> "_value" starts at col 25, length 6 -> col 31.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class C
            {
                private int _value;
                public int Read() => _value;
            }
            """));

        var edge = extraction.SingleEdge("Reads", "global::N.C.Read", "global::N.C._value");
        GoldenEdgeTests.AssertEdgeContract(edge, "Reads", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 25, 4, 31));
    }

    [Fact]
    public async Task Writes_SiteIsTheAssignmentTargetReference()
    {
        // Line 4: "    public void Write(int v) { _value = v; }"
        // "    public void Write(int v) { " = 31 chars -> "_value" starts at col 31, length 6 -> col 37.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class C
            {
                private int _value;
                public void Write(int v) { _value = v; }
            }
            """));

        var edge = extraction.SingleEdge("Writes", "global::N.C.Write", "global::N.C._value");
        GoldenEdgeTests.AssertEdgeContract(edge, "Writes", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 31, 4, 37));
    }

    [Fact]
    public async Task References_ParameterType_SiteIsTheParameterTypeSyntax()
    {
        // Line 4: "    public void Use(Arg value) { }"
        // "    public void Use(" = 20 chars -> "Arg" starts at col 20, length 3 -> col 23.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Arg { }
            public class C
            {
                public void Use(Arg value) { }
            }
            """));

        var edge = extraction.SingleEdge("References", "global::N.C.Use", "global::N.Arg");
        GoldenEdgeTests.AssertEdgeContract(edge, "References", Provenance.CompilerProved,
            ExtractorConstants.ParameterDependenciesExtractor, Doc, (4, 20, 4, 23));
    }

    [Fact]
    public async Task References_SignatureTypeArgument_SiteIsTheTypeDeclaration()
    {
        // Line 3: "public class Derived : IBox<Arg> { }" -> col 0..36 (36 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Arg { }
            public interface IBox<T> { }
            public class Derived : IBox<Arg> { }
            """));

        var edge = extraction.SingleEdge("References", "global::N.Derived", "global::N.Arg");
        GoldenEdgeTests.AssertEdgeContract(edge, "References", Provenance.CompilerProved,
            VersionConstants.ExtractorVersion, Doc, (3, 0, 3, 36));
    }

    [Fact]
    public async Task References_StaticReceiver_SiteIsTheReceiverExpression()
    {
        // Line 4: "    public void Run() { Helper.Ping(); }"
        // "    public void Run() { " = 24 chars -> "Helper" starts at col 24, length 6 -> col 30.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public static class Helper { public static void Ping() { } }
            public class C
            {
                public void Run() { Helper.Ping(); }
            }
            """));

        var edge = extraction.SingleEdge("References", "global::N.C.Run", "global::N.Helper");
        GoldenEdgeTests.AssertEdgeContract(edge, "References", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 24, 4, 30));
    }

    [Fact]
    public async Task References_AttributeArgument_SiteIsTheAttributeArgumentCast()
    {
        // Line 5: "    [Tag((Arg?)null)]" -> cast expression "(Arg?)null" starts after "    [Tag(" (9 chars) -> col 9..19.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Arg { }
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class TagAttribute : System.Attribute { public TagAttribute(object? o) { } }
            public class C
            {
                [Tag((Arg?)null)]
                public void M() { }
            }
            """));

        var edge = extraction.SingleEdge("References", "global::N.C.M", "global::N.Arg");
        GoldenEdgeTests.AssertEdgeContract(edge, "References", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (6, 9, 6, 19));
    }

    [Fact]
    public async Task Returns_SiteIsTheMethodDeclaration()
    {
        // Line 4: "    public Item Make() => new Item();" -> col 4..37 (33 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Item { }
            public class C
            {
                public Item Make() => new Item();
            }
            """));

        var edge = extraction.SingleEdge("Returns", "global::N.C.Make", "global::N.Item");
        GoldenEdgeTests.AssertEdgeContract(edge, "Returns", Provenance.CompilerProved,
            ExtractorConstants.ReturnsExtractor, Doc, (4, 4, 4, 37));
    }

    [Fact]
    public async Task Declares_SiteIsTheMemberDeclaration()
    {
        // Line 3: "    public void M() { }" -> col 4..23 (19 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class C
            {
                public void M() { }
            }
            """));

        var edge = extraction.SingleEdge("Declares", "global::N.C", "global::N.C.M");
        GoldenEdgeTests.AssertEdgeContract(edge, "Declares", Provenance.CompilerProved,
            ExtractorConstants.DeclaresExtractor, Doc, (3, 4, 3, 23));
    }

    [Fact]
    public async Task Inherits_SiteIsTheDerivedTypeDeclaration()
    {
        // Line 2: "public class Derived : Base { }" -> col 0..31 (31 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Base { }
            public class Derived : Base { }
            """));

        var edge = extraction.SingleEdge("Inherits", "global::N.Derived", "global::N.Base");
        GoldenEdgeTests.AssertEdgeContract(edge, "Inherits", Provenance.CompilerProved,
            VersionConstants.ExtractorVersion, Doc, (2, 0, 2, 31));
    }

    [Fact]
    public async Task Implements_SiteIsTheImplementingTypeDeclaration()
    {
        // Line 2: "public class Impl : IContract { }" -> col 0..33 (33 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public interface IContract { }
            public class Impl : IContract { }
            """));

        var edge = extraction.SingleEdge("Implements", "global::N.Impl", "global::N.IContract");
        GoldenEdgeTests.AssertEdgeContract(edge, "Implements", Provenance.CompilerProved,
            VersionConstants.ExtractorVersion, Doc, (2, 0, 2, 33));
    }

    [Fact]
    public async Task Overrides_SiteIsTheOverridingMemberDeclaration()
    {
        // Line 3: "    public override void M() { }" -> col 4..32 (28 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Base { public virtual void M() { } }
            public class Derived : Base
            {
                public override void M() { }
            }
            """));

        var edge = extraction.SingleEdge("Overrides", "global::N.Derived.M", "global::N.Base.M");
        GoldenEdgeTests.AssertEdgeContract(edge, "Overrides", Provenance.CompilerProved,
            ExtractorConstants.OverridesExtractor, Doc, (4, 4, 4, 32));
    }

    [Fact]
    public async Task Hides_SiteIsTheHidingMemberDeclaration()
    {
        // Line 4: "    public new void M() { }" -> col 4..27 (23 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Base { public void M() { } }
            public class Derived : Base
            {
                public new void M() { }
            }
            """));

        var edge = extraction.SingleEdge("Hides", "global::N.Derived.M", "global::N.Base.M");
        GoldenEdgeTests.AssertEdgeContract(edge, "Hides", Provenance.CompilerProved,
            ExtractorConstants.HidesExtractor, Doc, (4, 4, 4, 27));
    }

    [Fact]
    public async Task MayDispatchTo_InterfaceAndVirtualOverride_SitesAreTheImplementingMembers()
    {
        // Line 4: "    public virtual void M() { }" -> col 4..31 (27 chars) for IService.M -> Base.M.
        // Line 8: "    public override void M() { }" -> col 4..32 (28 chars) for Base.M -> Derived.M.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public interface IService { void M(); }
            public class Base : IService
            {
                public virtual void M() { }
            }
            public class Derived : Base
            {
                public override void M() { }
            }
            """));

        var ifaceEdge = Assert.Single(EdgeMerge.CollapseBatch(
            extraction.Result.Edges.Where(e =>
                e.Kind == "MayDispatchTo" &&
                e.SourceSymbolId == extraction.ResolveId("global::N.IService.M") &&
                e.TargetSymbolId == extraction.ResolveId("global::N.Base.M"))));
        GoldenEdgeTests.AssertEdgeContract(ifaceEdge, "MayDispatchTo", Provenance.CompilerProved,
            ExtractorConstants.PolymorphismExtractor, Doc, (4, 4, 4, 31));

        var overrideEdge = extraction.SingleEdge("MayDispatchTo", "global::N.Base.M", "global::N.Derived.M");
        GoldenEdgeTests.AssertEdgeContract(overrideEdge, "MayDispatchTo", Provenance.CompilerProved,
            ExtractorConstants.PolymorphismExtractor, Doc, (8, 4, 8, 32));
    }

    [Fact]
    public async Task Throws_SiteIsTheThrowStatement()
    {
        // Line 4: "    public void Fail() { throw new CustomException(); }"
        // "    public void Fail() { " = 25 chars -> "throw new CustomException();" = 28 chars -> col 25..53.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class CustomException : System.Exception { }
            public class C
            {
                public void Fail() { throw new CustomException(); }
            }
            """));

        var edge = extraction.SingleEdge("Throws", "global::N.C.Fail", "global::N.CustomException");
        GoldenEdgeTests.AssertEdgeContract(edge, "Throws", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 25, 4, 53));
    }

    [Fact]
    public async Task StaticallyCalls_SiteIsTheDispatchingInvocation()
    {
        // Line 5: "    public void Run(IService s) { s.Work(); }"
        // "    public void Run(IService s) { " = 34 chars -> "s.Work()" = 8 chars -> col 34..42.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public interface IService { void Work(); }
            public class C
            {
                public void Run(IService s) { s.Work(); }
            }
            """));

        var edge = extraction.SingleEdge("StaticallyCalls", "global::N.C.Run", "global::N.IService.Work");
        GoldenEdgeTests.AssertEdgeContract(edge, "StaticallyCalls", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 34, 4, 42));
    }

    [Fact]
    public async Task MethodGroupRef_SiteIsTheMethodGroupIdentifier()
    {
        // Line 4: "    public System.Action Get() => Target;"
        // "    public System.Action Get() => " = 34 chars -> "Target" = 6 chars -> col 34..40.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class C
            {
                public void Target() { }
                public System.Action Get() => Target;
            }
            """));

        var edge = extraction.SingleEdge("MethodGroupRef", "global::N.C.Get", "global::N.C.Target");
        GoldenEdgeTests.AssertEdgeContract(edge, "MethodGroupRef", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (4, 34, 4, 40));
    }

    [Fact]
    public async Task ExtensionReceiver_SiteIsTheExtensionInvocation()
    {
        // Line 6: "    public void Use(Target t) { t.Extend(); }"
        // "    public void Use(Target t) { " = 32 chars -> "t.Extend()" = 10 chars -> col 32..42.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Target { }
            public static class Ext { public static void Extend(this Target t) { } }
            public class C
            {
                public void Use(Target t) { t.Extend(); }
            }
            """));

        var edge = extraction.SingleEdge("ExtensionReceiver", "global::N.Target", "global::N.Ext.Extend");
        GoldenEdgeTests.AssertEdgeContract(edge, "ExtensionReceiver", Provenance.CompilerProved,
            ExtractorConstants.OperationShapesExtractor, Doc, (5, 32, 5, 42));
    }

    [Fact]
    public async Task Contains_SiteIsTheOuterTypeDeclaration()
    {
        // Line 1: "public class Outer { public class Inner { } }" -> col 0..45 (45 chars).
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Outer { public class Inner { } }
            """));

        var edge = extraction.SingleEdge("Contains", "global::N.Outer", "global::N.Outer.Inner");
        GoldenEdgeTests.AssertEdgeContract(edge, "Contains", Provenance.CompilerProved,
            VersionConstants.ExtractorVersion, Doc, (1, 0, 1, 45));
    }

    [Fact]
    public async Task ReflectionKinds_SitesAreTypeOfNameOfAndStringLiteral()
    {
        // Line 6: "        var t = typeof(Target);" -> "typeof(Target)" = col 16..30.
        // Line 7: "        var n = nameof(Target.Run);" -> "nameof(Target.Run)" = col 16..34.
        // Line 8: '        t.GetMethod("Run");' -> '"Run"' = col 20..25.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Target { public void Run() { } }
            public class C
            {
                public void Use()
                {
                    var t = typeof(Target);
                    var n = nameof(Target.Run);
                    t.GetMethod("Run");
                }
            }
            """));

        var typeRef = extraction.SingleEdge("ReflectionTypeRef", "global::N.C.Use", "global::N.Target");
        GoldenEdgeTests.AssertEdgeContract(typeRef, "ReflectionTypeRef", Provenance.CompilerProved,
            ExtractorConstants.ReflectionExtractor, Doc, (6, 16, 6, 30));

        var memberRef = extraction.SingleEdge("ReflectionMemberRef", "global::N.C.Use", "global::N.Target.Run");
        GoldenEdgeTests.AssertEdgeContract(memberRef, "ReflectionMemberRef", Provenance.CompilerProved,
            ExtractorConstants.ReflectionExtractor, Doc, (7, 16, 7, 34));

        var candidate = extraction.SingleEdge("ReflectionNameCandidate", "global::N.C.Use", "global::N.Target.Run",
            Provenance.NameCandidate);
        GoldenEdgeTests.AssertEdgeContract(candidate, "ReflectionNameCandidate", Provenance.NameCandidate,
            ExtractorConstants.ReflectionExtractor, Doc, (8, 20, 8, 25));

        var unknown = extraction.SingleEdge("ReflectionTargetUnknown", "global::N.C.Use", "global::N.C.Use",
            Provenance.RuntimeUnknown);
        // Line 8: '        t.GetMethod("Run");' -> invocation "t.GetMethod(\"Run\")" = col 8..26.
        GoldenEdgeTests.AssertEdgeContract(unknown, "ReflectionTargetUnknown", Provenance.RuntimeUnknown,
            ExtractorConstants.ReflectionExtractor, Doc, (8, 8, 8, 26));
    }
}
