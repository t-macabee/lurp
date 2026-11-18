using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     Phase 1 golden tests for the reflection extractors: typeof, nameof, and
///     string-literal name candidates. Pattern B (in-memory compilation).
/// </summary>
public sealed class GoldenReflectionTests : InMemoryTestBase
{
    private const string Doc = "Source.cs";

    private static Dictionary<string, string> One(string source)
    {
        return new Dictionary<string, string> { [Doc] = source };
    }

    private static void AssertReflectionContract(EdgeRecord edge, string kind, string provenance, string sourceFqn)
    {
        Assert.Equal(kind, edge.Kind);
        Assert.Equal(provenance, edge.Provenance);
        Assert.Equal("reflection-v4", edge.ExtractorVersion);
        Assert.NotNull(edge.SourceDocumentPath);
        Assert.EndsWith(Doc, edge.SourceDocumentPath);
        Assert.Equal(sourceFqn, edge.SourceSymbolId);
    }

    private static void AssertNoNameCandidate(Extraction extraction, string sourceFqn)
    {
        var sourceId = extraction.ResolveId(sourceFqn);
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == sourceId);
    }

    private static void AssertNoNameCandidateTo(Extraction extraction, string sourceFqn, string targetFqn)
    {
        var sourceId = extraction.ResolveId(sourceFqn);
        var targetId = extraction.ResolveId(targetFqn);
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == sourceId && e.TargetSymbolId == targetId);
    }

    [Fact]
    public async Task ReflectionTypeRef_TypeofExpression()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target { }
                                                public class User
                                                {
                                                    public void Use() { var t = typeof(Target); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("ReflectionTypeRef", "global::N.User.Use", "global::N.Target");
        AssertReflectionContract(edge, "ReflectionTypeRef", Provenance.CompilerProved,
            extraction.ResolveId("global::N.User.Use"));
    }

    [Fact]
    public async Task ReflectionMemberRef_NameofExpression()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target
                                                {
                                                    public string Name { get; set; }
                                                }
                                                public class User
                                                {
                                                    public void Use() { var n = nameof(Target.Name); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("ReflectionMemberRef", "global::N.User.Use", "global::N.Target.Name");
        AssertReflectionContract(edge, "ReflectionMemberRef", Provenance.CompilerProved,
            extraction.ResolveId("global::N.User.Use"));
    }

    [Fact]
    public async Task NameOfExpression_DoesNotRecordUnsupportedSyntax()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target
                                                {
                                                    public string Name { get; set; }
                                                }
                                                public class User
                                                {
                                                    public void Use() { var n = nameof(Target.Name); }
                                                }
                                                """));

        // nameof(...) is fully handled by NameOfReflectionExtractor, so
        // CallsEdgeExtractor must not record it as an unsupported_syntax
        // binding-incompleteness region (which would falsely mark the document's
        // binding region unobservable and flip its empty tiers to "unresolved").
        Assert.DoesNotContain(extraction.Result.BindingIncompleteness,
            r => r.Reason == "unsupported_syntax" && r.DocumentPath?.EndsWith(Doc, StringComparison.Ordinal) == true);

        // The reflection member-reference edge is still emitted for the nameof.
        var edge = extraction.SingleEdge("ReflectionMemberRef", "global::N.User.Use", "global::N.Target.Name");
        AssertReflectionContract(edge, "ReflectionMemberRef", Provenance.CompilerProved,
            extraction.ResolveId("global::N.User.Use"));
    }

    [Fact]
    public async Task ReflectionEdges_AreOwnedByTheMemberThatRunsTheCode()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                namespace N;
                                                public class Pb8Idx { } public class Pb8Op { } public class Pb8Tail { } public class Pb8Ev { }
                                                public class Pb8OnType { } public class Pb8OnMethod { } public class Pb8Multi1 { } public class Pb8Multi2 { }
                                                public class Pb8Acc { } public class Pb8Prop { } public class Pb8Init { }
                                                public sealed class Pb8Attr : Attribute { public Pb8Attr(Type t) { } }
                                                [Pb8Attr(typeof(Pb8OnType))]
                                                public class Refl8
                                                {
                                                    public Type this[int i] => typeof(Pb8Idx);
                                                    public static Type operator +(Refl8 a, Refl8 b) => typeof(Pb8Op);
                                                    ~Refl8() { var t = typeof(Pb8Tail); }
                                                    public event Action Ev { add { var t = typeof(Pb8Ev); } remove { } }
                                                    public object F1 = typeof(Pb8Multi1), F2 = typeof(Pb8Multi2);
                                                    public Type P2 { get { return typeof(Pb8Acc); } }
                                                    public Type P1 => typeof(Pb8Prop);
                                                    public Type P3 { get; } = typeof(Pb8Init);
                                                    [Pb8Attr(typeof(Pb8OnMethod))]
                                                    public void Mark() { }
                                                    public string N1 => nameof(P2);
                                                    public object M1 => typeof(Refl8).GetMethod("Mark");
                                                }
                                                """));

        // D8, D20: the member that runs the code owns the fact. An accessor owns its body (an
        // expression-bodied property or indexer included), each variable owns its own initializer,
        // a property owns its initializer, the type owns a type attribute, and a member owns its
        // attribute.
        extraction.AssertDocIdEdgeSet("ReflectionTypeRef",
            ("M:N.Refl8.get_Item(System.Int32)", "T:N.Pb8Idx"),
            ("M:N.Refl8.op_Addition(N.Refl8,N.Refl8)", "T:N.Pb8Op"),
            ("M:N.Refl8.Finalize", "T:N.Pb8Tail"),
            ("M:N.Refl8.add_Ev(System.Action)", "T:N.Pb8Ev"),
            ("T:N.Refl8", "T:N.Pb8OnType"),
            ("F:N.Refl8.F1", "T:N.Pb8Multi1"),
            ("F:N.Refl8.F2", "T:N.Pb8Multi2"),
            ("M:N.Refl8.get_P2", "T:N.Pb8Acc"),
            ("M:N.Refl8.get_P1", "T:N.Pb8Prop"),
            ("P:N.Refl8.P3", "T:N.Pb8Init"),
            ("M:N.Refl8.Mark", "T:N.Pb8OnMethod"),
            ("M:N.Refl8.get_M1", "T:N.Refl8"));
        extraction.AssertDocIdEdgeSet("ReflectionMemberRef", ("M:N.Refl8.get_N1", "P:N.Refl8.P2"));
        extraction.AssertDocIdEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate,
            ("M:N.Refl8.get_M1", "M:N.Refl8.Mark"));
        extraction.AssertDocIdEdgeSetOf("ReflectionTargetUnknown", Provenance.RuntimeUnknown,
            ("M:N.Refl8.get_M1", "M:N.Refl8.get_M1"));
    }

    [Fact]
    public async Task UnresolvableInvocation_StillRecordsCompilerError()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class User
                                                {
                                                    public void Use() { MissingMethod(); }
                                                }
                                                """));

        // A genuinely unresolvable invocation must still record a binding
        // incompleteness for the document — compiler_error (CS0103), not
        // unsupported_syntax — proving the nameof skip does not over-broaden
        // to every invocation without a method symbol.
        Assert.Contains(extraction.Result.BindingIncompleteness,
            r => r.DocumentPath?.EndsWith(Doc, StringComparison.Ordinal) == true && r.Reason == "compiler_error");
    }

    [Fact]
    public async Task BareStringLiteral_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target { }
                                                public class User
                                                {
                                                    public void Use() { var name = "Target"; }
                                                }
                                                """));

        AssertNoNameCandidate(extraction, "global::N.User.Use");
    }

    [Fact]
    public async Task TypeGetType_MatchesTheNamespaceQualifiedName()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                namespace N;
                                                public class Target { }
                                                public class User
                                                {
                                                    public void Use() { var t = Type.GetType("N.Target"); }
                                                    public void UseSimpleName() { var t = Type.GetType("Target"); }
                                                }
                                                """));

        // D144 (.NET docs, Type.GetType(String)): the name is namespace-qualified; a simple name
        // finds only a type of the global namespace, so "Target" does not bind N.Target.
        var edge = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Target",
            Provenance.NameCandidate);
        AssertReflectionContract(edge, "ReflectionNameCandidate", Provenance.NameCandidate,
            extraction.ResolveId("global::N.User.Use"));
        AssertNoNameCandidate(extraction, "global::N.User.UseSimpleName");
    }

    [Fact]
    public async Task AllThreeReflectionKinds_FromOneSource()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target
                                                {
                                                    public string Name { get; set; }
                                                    public void Run() { }
                                                }
                                                public class User
                                                {
                                                    public void Use()
                                                    {
                                                        var t = typeof(Target);
                                                        var n = nameof(Target.Name);
                                                        var m = typeof(Target).GetMethod("Run");
                                                    }
                                                }
                                                """));

        var sourceId = extraction.ResolveId("global::N.User.Use");
        var targetTypeId = extraction.ResolveId("global::N.Target");
        var typeRefs = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionTypeRef" && e.SourceSymbolId == sourceId && e.TargetSymbolId == targetTypeId)
            .ToList();
        Assert.Equal(2, typeRefs.Count);
        Assert.All(typeRefs, edge => Assert.Equal("compiler_proved", edge.Provenance));

        var memberRef = extraction.SingleEdge("ReflectionMemberRef", "global::N.User.Use", "global::N.Target.Name");
        Assert.Equal("compiler_proved", memberRef.Provenance);

        var nameCandidate = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Target.Run",
            Provenance.NameCandidate);
        Assert.Equal("reflection-v4", nameCandidate.ExtractorVersion);
    }

    [Fact]
    public async Task PropertyChangedLiteral_NamesOnlyAProperty()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.ComponentModel;
                                                namespace N;
                                                public class AccessController
                                                {
                                                    public void Login()
                                                    {
                                                        var x = new PropertyChangedEventArgs("Login");
                                                    }
                                                    public void Logout()
                                                    {
                                                        var y = new PropertyChangedEventArgs("Logout");
                                                    }
                                                }
                                                """));

        // D143 (.NET docs, PropertyChangedEventArgs.PropertyName): the name is the name of a
        // property. Login and Logout are methods, so neither literal is a candidate.
        extraction.AssertEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate);
    }

    [Fact]
    public async Task NameCandidate_ToItsOwnOwner_IsRecorded()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.ComponentModel;
                                                namespace N;
                                                public class Refl38
                                                {
                                                    public void Run() { typeof(Refl38).GetMethod("Run"); }
                                                    public void Other() { typeof(Refl38).GetMethod("Run"); }
                                                }
                                                public class Vm38 : INotifyPropertyChanged
                                                {
                                                    public event PropertyChangedEventHandler? PropertyChanged;
                                                    public string Name
                                                    {
                                                        get => "";
                                                        set { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Name")); }
                                                    }
                                                    public void Touch() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Name")); }
                                                }
                                                """));

        // D13, D134: no extractor drops an edge from a member to itself; the readers ignore it.
        // D20: the setter owns its body, so its candidate starts at the setter.
        extraction.AssertEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate,
            ("global::N.Refl38.Other", "global::N.Refl38.Run"),
            ("global::N.Refl38.Run", "global::N.Refl38.Run"),
            ("global::N.Vm38.Name.set", "global::N.Vm38.Name"),
            ("global::N.Vm38.Touch", "global::N.Vm38.Name"));
    }

    [Fact]
    public async Task ShortMemberName_IsACandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace Microsoft.EntityFrameworkCore.Metadata.Builders
                                                {
                                                    public class EntityTypeBuilder<T>
                                                    {
                                                        public void HasKey(params string[] names) { }
                                                    }
                                                }
                                                namespace N
                                                {
                                                    using Microsoft.EntityFrameworkCore.Metadata.Builders;
                                                    public class Order
                                                    {
                                                        public int Id { get; set; }
                                                        public int X { get; set; }
                                                    }
                                                    public class User
                                                    {
                                                        public void Use() { var p = typeof(Order).GetProperty("X"); }
                                                        public void Configure(EntityTypeBuilder<Order> b) { b.HasKey("Id"); }
                                                    }
                                                }
                                                """));

        // D17: no length cutoff. GetProperty("X") binds the property X (.NET reflection docs), and
        // EF Core HasKey("Id") names the key property Id.
        extraction.AssertEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate,
            ("global::N.User.Configure", "global::N.Order.Id"),
            ("global::N.User.Use", "global::N.Order.X"));
    }

    [Fact]
    public async Task MemberLookups_MatchCaseAndMemberKind()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                using System.Reflection;
                                                namespace N;
                                                public class K12
                                                {
                                                    public void Run() { }
                                                    public string Code { get; set; } = "";
                                                    public int Counter;
                                                    public event Action? Changed;
                                                    public class Inner { }
                                                }
                                                public class User
                                                {
                                                    public void Case() { typeof(K12).GetMethod("run"); typeof(K12).GetMethod("RUN"); }
                                                    public void IgnoreCase() { typeof(K12).GetMethod("run", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase); }
                                                    public void MethodOnProperty() { typeof(K12).GetMethod("Code"); }
                                                    public void PropertyOnMethod() { typeof(K12).GetProperty("Run"); }
                                                    public void MethodOnField() { typeof(K12).GetMethod("Counter"); }
                                                    public void Kinds()
                                                    {
                                                        typeof(K12).GetMethod("Run");
                                                        typeof(K12).GetProperty("Code");
                                                        typeof(K12).GetField("Counter");
                                                        typeof(K12).GetEvent("Changed");
                                                        typeof(K12).GetNestedType("Inner");
                                                    }
                                                    public void AnyKind() { typeof(K12).GetMember("Counter"); }
                                                }
                                                """));

        // D21 (.NET reflection docs): the name matches case-sensitively unless the call passes
        // BindingFlags.IgnoreCase, and GetMethod, GetProperty, GetField, GetEvent and GetNestedType
        // each bind only their member kind. GetMember binds any member.
        extraction.AssertEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate,
            ("global::N.User.AnyKind", "global::N.K12.Counter"),
            ("global::N.User.IgnoreCase", "global::N.K12.Run"),
            ("global::N.User.Kinds", "global::N.K12.Changed"),
            ("global::N.User.Kinds", "global::N.K12.Code"),
            ("global::N.User.Kinds", "global::N.K12.Counter"),
            ("global::N.User.Kinds", "global::N.K12.Inner"),
            ("global::N.User.Kinds", "global::N.K12.Run"));
    }

    [Fact]
    public async Task RuntimeNameApis_AreSinksByTheirBoundMethod()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                using System.Reflection;
                                                namespace N;
                                                public interface IRefl { }
                                                public class Refl : IRefl
                                                {
                                                    public void Target() { }
                                                    public int Value { get; set; }
                                                }
                                                public class Base10 { public void Inherited() { } }
                                                public class Der10 : Base10 { }
                                                public class Type2 { public object GetType(string name) => name; }
                                                public class Holder
                                                {
                                                    public Type2 Type => new Type2();
                                                    public void MakeGenericMethod(System.Type t) { }
                                                    public void Fake() { Type.GetType("N.Refl"); MakeGenericMethod(typeof(int)); }
                                                }
                                                public class User
                                                {
                                                    public void Iface() { typeof(Refl).GetInterface("IRefl"); }
                                                    public void Runtime() { typeof(Refl).GetRuntimeMethod("Target", Type.EmptyTypes); typeof(Refl).GetRuntimeProperty("Value"); }
                                                    public void Declared() { typeof(Refl).GetTypeInfo().GetDeclaredMethod("Target"); typeof(Der10).GetTypeInfo().GetDeclaredMethod("Inherited"); }
                                                    public void Inherit() { typeof(Der10).GetMethod("Inherited"); }
                                                    public void ByName() { Type.GetType("N.Refl"); Type.GetType("Refl"); Type.GetType("N.Refl, App"); }
                                                }
                                                """));

        // D36, D143: a sink is the bound runtime API (Type.GetInterface, RuntimeReflectionExtensions,
        // TypeInfo.GetDeclared*), not a member name in the syntax, so Holder's own GetType and
        // MakeGenericMethod are no sinks. GetMethod also finds an inherited public method; a
        // GetDeclared* call finds only a member that the type declares (.NET reflection docs).
        // D144: Type.GetType reads a namespace-qualified name, optionally assembly-qualified.
        extraction.AssertEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate,
            ("global::N.User.ByName", "global::N.Refl"),
            ("global::N.User.Declared", "global::N.Refl.Target"),
            ("global::N.User.Iface", "global::N.IRefl"),
            ("global::N.User.Inherit", "global::N.Base10.Inherited"),
            ("global::N.User.Runtime", "global::N.Refl.Target"),
            ("global::N.User.Runtime", "global::N.Refl.Value"));
        extraction.AssertEdgeSetOf("ReflectionTargetUnknown", Provenance.RuntimeUnknown,
            ("global::N.User.ByName", "global::N.User.ByName"),
            ("global::N.User.Declared", "global::N.User.Declared"),
            ("global::N.User.Iface", "global::N.User.Iface"),
            ("global::N.User.Inherit", "global::N.User.Inherit"),
            ("global::N.User.Runtime", "global::N.User.Runtime"));
    }

    [Fact]
    public async Task TypeGetProperty_WithoutKnownType_EmitsAllMatchingMembersInSymbolIdOrder()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                namespace N;
                                                public class TypeA
                                                {
                                                    public string Name { get; set; }
                                                }
                                                public class TypeB
                                                {
                                                    public string Name { get; set; }
                                                }
                                                public class User
                                                {
                                                    public void Use(Type t)
                                                    {
                                                        var s = t.GetProperty("Name");
                                                    }
                                                }
                                                """));

        var userUseId = extraction.ResolveId("global::N.User.Use");
        var edges = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == userUseId)
            .ToList();

        Assert.Equal(2, edges.Count);
        Assert.All(edges, e =>
        {
            AssertReflectionContract(e, "ReflectionNameCandidate", Provenance.NameCandidate, userUseId);
        });

        var idA = extraction.ResolveId("global::N.TypeA.Name");
        var idB = extraction.ResolveId("global::N.TypeB.Name");

        var expectedIds = new[] { idA, idB }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedIds[0], edges[0].TargetSymbolId);
        Assert.Equal(expectedIds[1], edges[1].TargetSymbolId);
    }

    [Fact]
    public async Task TypeMemberLookups_OnTypeof_LimitCandidatesToTheKnownType()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                namespace N;
                                                public class Owner
                                                {
                                                    public class Inner { }
                                                    public void Run() { }
                                                    public string Code { get; set; }
                                                }
                                                public class Other
                                                {
                                                    public class Inner { }
                                                    public void Run() { }
                                                    public string Code { get; set; }
                                                }
                                                public class User
                                                {
                                                    public void Use()
                                                    {
                                                        var m = typeof(Owner).GetMethod("Run");
                                                        var p = typeof(Owner).GetProperty("Code");
                                                        var t = typeof(Owner).GetNestedType("Inner");
                                                    }
                                                }
                                                """));

        var userId = extraction.ResolveId("global::N.User.Use");
        var edges = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == userId)
            .ToList();

        Assert.Equal(3, edges.Count);
        Assert.Contains(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Owner.Run"));
        Assert.Contains(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Owner.Code"));
        Assert.Contains(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Owner.Inner"));
        Assert.DoesNotContain(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Other.Run"));
        Assert.DoesNotContain(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Other.Code"));
        Assert.DoesNotContain(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Other.Inner"));
    }

    [Fact]
    public async Task ReflectionNameCandidate_MemberOfNestedType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Owner
                                                {
                                                    public class Inner
                                                    {
                                                        public void Run() { }
                                                    }
                                                }
                                                public class User
                                                {
                                                    public void Use()
                                                    {
                                                        var m = typeof(Owner.Inner).GetMethod("Run");
                                                    }
                                                }
                                                """));

        extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Owner.Inner.Run",
            Provenance.NameCandidate);
    }

    [Fact]
    public async Task PropertyChangedEventArgs_LimitsCandidateToContainingType()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.ComponentModel;
                                                namespace N;
                                                public class User
                                                {
                                                    public string Name { get; set; }
                                                    public void Use()
                                                    {
                                                        var e = new PropertyChangedEventArgs("Name");
                                                    }
                                                }
                                                public class Target
                                                {
                                                    public string Name { get; set; }
                                                }
                                                """));

        var edge = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.User.Name",
            Provenance.NameCandidate);
        AssertReflectionContract(edge, "ReflectionNameCandidate", Provenance.NameCandidate,
            extraction.ResolveId("global::N.User.Use"));
        AssertNoNameCandidateTo(extraction, "global::N.User.Use", "global::N.Target.Name");
    }

    [Fact]
    public async Task CallerMemberNameArgument_LimitsCandidateToContainingType()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.Runtime.CompilerServices;
                                                namespace N;
                                                public class User
                                                {
                                                    public string Name { get; set; }
                                                    public void Notify([CallerMemberName] string propertyName = null)
                                                    {
                                                    }
                                                    public void Use()
                                                    {
                                                        Notify("Name");
                                                    }
                                                }
                                                public class Target
                                                {
                                                    public string Name { get; set; }
                                                }
                                                """));

        var edge = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.User.Name",
            Provenance.NameCandidate);
        AssertReflectionContract(edge, "ReflectionNameCandidate", Provenance.NameCandidate,
            extraction.ResolveId("global::N.User.Use"));
        AssertNoNameCandidateTo(extraction, "global::N.User.Use", "global::N.Target.Name");
    }

    [Fact]
    public async Task EntityBuilderStringApi_LimitsCandidateToEntityType()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace Microsoft.EntityFrameworkCore.Metadata.Builders
                                                {
                                                    public class EntityTypeBuilder<T>
                                                    {
                                                        public void Property(string name) { }
                                                        public void HasKey(params string[] names) { }
                                                    }
                                                }
                                                namespace N
                                                {
                                                    using Microsoft.EntityFrameworkCore.Metadata.Builders;
                                                    public class Order
                                                    {
                                                        public int Key { get; set; }
                                                        public string Code { get; set; }
                                                    }
                                                    public class Other
                                                    {
                                                        public int Key { get; set; }
                                                        public string Code { get; set; }
                                                    }
                                                    public class Config
                                                    {
                                                        public void Configure(EntityTypeBuilder<Order> b)
                                                        {
                                                            b.Property("Code");
                                                            b.HasKey("Key");
                                                        }
                                                    }
                                                }
                                                """));

        var codeEdge = extraction.SingleEdge("ReflectionNameCandidate", "global::N.Config.Configure",
            "global::N.Order.Code", Provenance.NameCandidate);
        AssertReflectionContract(codeEdge, "ReflectionNameCandidate", Provenance.NameCandidate,
            extraction.ResolveId("global::N.Config.Configure"));

        extraction.SingleEdge("ReflectionNameCandidate", "global::N.Config.Configure", "global::N.Order.Key",
            Provenance.NameCandidate);
        AssertNoNameCandidateTo(extraction, "global::N.Config.Configure", "global::N.Other.Code");
        AssertNoNameCandidateTo(extraction, "global::N.Config.Configure", "global::N.Other.Key");
    }

    [Fact]
    public async Task QueryableIncludeArgument_LimitsCandidateToReceiverElementType()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.Linq;
                                                namespace Microsoft.EntityFrameworkCore
                                                {
                                                    public static class EntityFrameworkQueryableExtensions
                                                    {
                                                        public static void Include<T>(this IQueryable<T> source, string navigationPropertyPath) { }
                                                    }
                                                }
                                                namespace N
                                                {
                                                    using System.Linq;
                                                    using Microsoft.EntityFrameworkCore;
                                                    public class Order
                                                    {
                                                        public string Code { get; set; }
                                                    }
                                                    public class Other
                                                    {
                                                        public string Code { get; set; }
                                                    }
                                                    public class Config
                                                    {
                                                        public void Configure(IQueryable<Order> orders)
                                                        {
                                                            orders.Include("Code");
                                                        }
                                                    }
                                                }
                                                """));

        extraction.SingleEdge("ReflectionNameCandidate", "global::N.Config.Configure", "global::N.Order.Code",
            Provenance.NameCandidate);
        AssertNoNameCandidateTo(extraction, "global::N.Config.Configure", "global::N.Other.Code");
    }

    [Fact]
    public async Task UntypedEntityBuilderSnapshot_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace Microsoft.EntityFrameworkCore.Metadata.Builders
                                                {
                                                    public class EntityTypeBuilder
                                                    {
                                                        public void Property(string name) { }
                                                    }
                                                }
                                                namespace N
                                                {
                                                    using Microsoft.EntityFrameworkCore.Metadata.Builders;
                                                    public class Order
                                                    {
                                                        public int Key { get; set; }
                                                    }
                                                    public class Snapshot
                                                    {
                                                        public void Build(EntityTypeBuilder b)
                                                        {
                                                            b.Property("Key");
                                                        }
                                                    }
                                                }
                                                """));

        AssertNoNameCandidate(extraction, "global::N.Snapshot.Build");
    }

    [Fact]
    public async Task ForeignKeyAndInverseProperty_LimitCandidatesToContainingType()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.ComponentModel.DataAnnotations.Schema;
                                                namespace N;
                                                public class User
                                                {
                                                    public int UserKey { get; set; }
                                                    [ForeignKey("UserKey")]
                                                    public int? ManagerKey { get; set; }
                                                }
                                                public class Profile
                                                {
                                                    public string Bio { get; set; }
                                                    [InverseProperty("Bio")]
                                                    public User Owner { get; set; }
                                                }
                                                public class Other
                                                {
                                                    public int UserKey { get; set; }
                                                    public string Bio { get; set; }
                                                }
                                                """));

        extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.ManagerKey", "global::N.User.UserKey",
            Provenance.NameCandidate);
        extraction.SingleEdge("ReflectionNameCandidate", "global::N.Profile.Owner", "global::N.Profile.Bio",
            Provenance.NameCandidate);
        AssertNoNameCandidateTo(extraction, "global::N.User.ManagerKey", "global::N.Other.UserKey");
        AssertNoNameCandidateTo(extraction, "global::N.Profile.Owner", "global::N.Other.Bio");
    }

    [Fact]
    public async Task MemberDataAttribute_LimitsCandidateToTestClassOrMemberType()
    {
        var extraction = await ExtractAsync(One("""
                                                using Xunit;
                                                namespace N;
                                                public class DataSource
                                                {
                                                    public static object[] Cases() { return new object[] { 1 }; }
                                                }
                                                public class OtherData
                                                {
                                                    public static object[] Cases() { return new object[] { 2 }; }
                                                }
                                                public class Tests
                                                {
                                                    public static object[] Cases() { return new object[] { 3 }; }

                                                    [MemberData("Cases")]
                                                    public void UseLocalData() { }

                                                    [MemberData("Cases", MemberType = typeof(DataSource))]
                                                    public void UseExternalData() { }
                                                }
                                                """));

        extraction.SingleEdge("ReflectionNameCandidate", "global::N.Tests.UseLocalData", "global::N.Tests.Cases",
            Provenance.NameCandidate);
        extraction.SingleEdge("ReflectionNameCandidate", "global::N.Tests.UseExternalData", "global::N.DataSource.Cases",
            Provenance.NameCandidate);
        AssertNoNameCandidateTo(extraction, "global::N.Tests.UseExternalData", "global::N.Tests.Cases");
        AssertNoNameCandidateTo(extraction, "global::N.Tests.UseLocalData", "global::N.DataSource.Cases");
        AssertNoNameCandidateTo(extraction, "global::N.Tests.UseExternalData", "global::N.OtherData.Cases");
        AssertNoNameCandidateTo(extraction, "global::N.Tests.UseLocalData", "global::N.OtherData.Cases");
    }

    [Fact]
    public async Task FrameworkNames_BindInheritedMembersOfTheirKind()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.ComponentModel;
                                                using System.ComponentModel.DataAnnotations.Schema;
                                                using Xunit;
                                                namespace N;
                                                public class BaseEntity { public int OwnerId { get; set; } }
                                                public class Owner { }
                                                public class Pet : BaseEntity
                                                {
                                                    [ForeignKey("OwnerId")]
                                                    public Owner? Owner { get; set; }
                                                }
                                                public record PetRec(int OwnerRecId, [property: ForeignKey("OwnerRecId")] Owner? O);
                                                public class DataBase { public static object[][] Rows => []; }
                                                public class MemberDataTests : DataBase
                                                {
                                                    [Theory]
                                                    [MemberData("Rows")]
                                                    public void Check(int x) { }
                                                }
                                                public class VmBase { public string Title { get; set; } = ""; }
                                                public class Vm : VmBase, INotifyPropertyChanged
                                                {
                                                    public event PropertyChangedEventHandler? PropertyChanged;
                                                    public void Raise()
                                                    {
                                                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Title"));
                                                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Raise"));
                                                    }
                                                }
                                                """));

        // D36: EF Core resolves [ForeignKey] on the entity with its inherited properties, xUnit
        // MemberData walks the base types, and a [property:] attribute on a positional record
        // parameter applies to the generated property. D143: PropertyChangedEventArgs names a
        // property, so "Raise" (a method) is no candidate.
        extraction.AssertEdgeSetOf("ReflectionNameCandidate", Provenance.NameCandidate,
            ("global::N.MemberDataTests.Check", "global::N.DataBase.Rows"),
            ("global::N.Pet.Owner", "global::N.BaseEntity.OwnerId"),
            ("global::N.PetRec.O", "global::N.PetRec.OwnerRecId"),
            ("global::N.Vm.Raise", "global::N.VmBase.Title"));
    }

    [Fact]
    public async Task MvcActionNameApis_LimitCandidatesToControllerTypes()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace Microsoft.AspNetCore.Mvc
                                                {
                                                    public class ControllerBase
                                                    {
                                                        public void RedirectToAction(string actionName) { }
                                                        public void CreatedAtAction(string actionName, object value) { }
                                                        public void AcceptedAtAction(string actionName, object value) { }
                                                    }
                                                    public interface IUrlHelper
                                                    {
                                                        void Action(string action);
                                                    }
                                                }
                                                namespace Microsoft.AspNetCore.Mvc.Rendering
                                                {
                                                    public interface IHtmlHelper
                                                    {
                                                        void ActionLink(string linkText, string actionName);
                                                    }
                                                }
                                                namespace N
                                                {
                                                    using Microsoft.AspNetCore.Mvc;
                                                    using Microsoft.AspNetCore.Mvc.Rendering;
                                                    public class HomeController : ControllerBase
                                                    {
                                                        public void Login() { }
                                                        public void ForgotPassword() { }
                                                    }
                                                    public class Other
                                                    {
                                                        public void Login() { }
                                                        public void ForgotPassword() { }
                                                    }
                                                    public class Caller
                                                    {
                                                        public void Use(ControllerBase controller, IUrlHelper url, IHtmlHelper html)
                                                        {
                                                            controller.RedirectToAction("Login");
                                                            controller.CreatedAtAction("ForgotPassword", new object());
                                                            url.Action("Login");
                                                            html.ActionLink("Go home", "ForgotPassword");
                                                        }
                                                    }
                                                }
                                                """));

        var callerUseId = extraction.ResolveId("global::N.Caller.Use");
        var edges = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == callerUseId)
            .ToList();

        Assert.Contains(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.HomeController.Login"));
        Assert.Contains(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.HomeController.ForgotPassword"));
        Assert.DoesNotContain(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Other.Login"));
        Assert.DoesNotContain(edges, e => e.TargetSymbolId == extraction.ResolveId("global::N.Other.ForgotPassword"));
    }

    [Fact]
    public async Task JsonElementGetProperty_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.Text.Json;
                                                namespace N;
                                                public class Source
                                                {
                                                    public string Name { get; set; }
                                                }
                                                public class User
                                                {
                                                    public void Use(JsonElement element)
                                                    {
                                                        var x = element.GetProperty("Name");
                                                    }
                                                }
                                                """));

        AssertNoNameCandidate(extraction, "global::N.User.Use");
    }

    [Fact]
    public async Task NonBindingAttributeArgument_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.Text.Json.Serialization;
                                                namespace N;
                                                public class Target
                                                {
                                                    public string Username { get; set; }
                                                }
                                                public class User
                                                {
                                                    [JsonPropertyName("Username")]
                                                    public string Name { get; set; }
                                                }
                                                """));

        AssertNoNameCandidate(extraction, "global::N.User.Name");
    }

    [Fact]
    public async Task MessageConstant_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Target
                                                {
                                                    public string Status { get; set; }
                                                }
                                                public static class Messages
                                                {
                                                    public const string ReportColumnStatus = "Status";
                                                }
                                                """));

        Assert.DoesNotContain(extraction.Result.Edges, e => e.Kind == "ReflectionNameCandidate");
    }

    [Fact]
    public async Task DictionaryKey_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                using System.Collections.Generic;
                                                namespace N;
                                                public class Target
                                                {
                                                    public string Status { get; set; }
                                                }
                                                public class User
                                                {
                                                    public void Use(Dictionary<string, string> map, string value)
                                                    {
                                                        map["Status"] = value;
                                                    }
                                                }
                                                """));

        AssertNoNameCandidate(extraction, "global::N.User.Use");
    }

    [Fact]
    public async Task PatternAndSwitchConstant_DoesNotEmitCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                namespace N;
                                                public class Request
                                                {
                                                    public string ActionName { get; set; }
                                                }
                                                public class HomeController
                                                {
                                                    public void Login() { }
                                                }
                                                public class User
                                                {
                                                    public bool IsLogin(Request request) => request is { ActionName: "Login" };
                                                    public int Map(string value) => value switch { "Login" => 1, _ => 0 };
                                                }
                                                """));

        AssertNoNameCandidate(extraction, "global::N.User.IsLogin");
        AssertNoNameCandidate(extraction, "global::N.User.Map");
    }

    [Fact]
    public async Task ReflectionTypeRef_RepeatedTypeOf_GivesOneRecordForEachSite()
    {
        // Line 6: "        var a = typeof(X);" -> typeof(X) spans col 16..25.
        // Line 7: "        var b = typeof(X);" -> typeof(X) spans col 16..25.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class X { }
            public class C
            {
                public void Use()
                {
                    var a = typeof(X);
                    var b = typeof(X);
                }
            }
            """));

        var sourceId = extraction.ResolveId("global::N.C.Use");
        var targetId = extraction.ResolveId("global::N.X");
        var sites = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionTypeRef" && e.SourceSymbolId == sourceId && e.TargetSymbolId == targetId)
            .OrderBy(e => e.SourceStartLine)
            .Select(e => (e.SourceStartLine, e.SourceStartColumn, e.SourceEndLine, e.SourceEndColumn))
            .ToList();

        Assert.Equal(
            [
                ((int?)6, (int?)16, (int?)6, (int?)25),
                ((int?)7, (int?)16, (int?)7, (int?)25)
            ],
            sites);
    }

    [Fact]
    public async Task ReflectionMemberRef_RepeatedNameOf_GivesOneRecordForEachSite()
    {
        // Line 6: "        var a = nameof(X);" -> nameof(X) spans col 16..25.
        // Line 7: "        var b = nameof(X);" -> nameof(X) spans col 16..25.
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class X { }
            public class C
            {
                public void Use()
                {
                    var a = nameof(X);
                    var b = nameof(X);
                }
            }
            """));

        var sourceId = extraction.ResolveId("global::N.C.Use");
        var targetId = extraction.ResolveId("global::N.X");
        var sites = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionMemberRef" && e.SourceSymbolId == sourceId && e.TargetSymbolId == targetId)
            .OrderBy(e => e.SourceStartLine)
            .Select(e => (e.SourceStartLine, e.SourceStartColumn, e.SourceEndLine, e.SourceEndColumn))
            .ToList();

        Assert.Equal(
            [
                ((int?)6, (int?)16, (int?)6, (int?)25),
                ((int?)7, (int?)16, (int?)7, (int?)25)
            ],
            sites);
    }

    [Fact]
    public async Task ReflectionNameCandidate_RepeatedStringLiteral_GivesOneRecordForEachSite()
    {
        // Line 7: '        typeof(Target).GetMethod("Run");' -> "Run" spans col 33..38.
        // Line 8: '        typeof(Target).GetMethod("Run");' -> "Run" spans col 33..38.
        var extraction = await ExtractAsync(One("""
            using System;
            namespace N;
            public class Target { public void Run() { } }
            public class User
            {
                public void Use()
                {
                    typeof(Target).GetMethod("Run");
                    typeof(Target).GetMethod("Run");
                }
            }
            """));

        var sourceId = extraction.ResolveId("global::N.User.Use");
        var targetId = extraction.ResolveId("global::N.Target.Run");
        var sites = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == sourceId && e.TargetSymbolId == targetId)
            .OrderBy(e => e.SourceStartLine)
            .Select(e => (e.SourceStartLine, e.SourceStartColumn, e.SourceEndLine, e.SourceEndColumn))
            .ToList();

        Assert.Equal(
            [
                ((int?)7, (int?)33, (int?)7, (int?)38),
                ((int?)8, (int?)33, (int?)8, (int?)38)
            ],
            sites);
    }

    [Fact]
    public async Task ReflectionTargetUnknown_RepeatedUnknownPattern_GivesOneRecordForEachSite()
    {
        // Line 6: "        Type.GetType(name);" -> Type.GetType(name) spans col 8..26 (18 chars).
        // Line 7: "        Type.GetType(name);" -> Type.GetType(name) spans col 8..26 (18 chars).
        var extraction = await ExtractAsync(One("""
            using System;
            namespace N;
            public class User
            {
                public void Use(string name)
                {
                    Type.GetType(name);
                    Type.GetType(name);
                }
            }
            """));

        var sourceId = extraction.ResolveId("global::N.User.Use");
        var sites = extraction.Result.Edges
            .Where(e => e.Kind == "ReflectionTargetUnknown" && e.SourceSymbolId == sourceId)
            .OrderBy(e => e.SourceStartLine)
            .Select(e => (e.SourceStartLine, e.SourceStartColumn, e.SourceEndLine, e.SourceEndColumn))
            .ToList();

        Assert.Equal(
            [
                ((int?)6, (int?)8, (int?)6, (int?)26),
                ((int?)7, (int?)8, (int?)7, (int?)26)
            ],
            sites);
    }
}