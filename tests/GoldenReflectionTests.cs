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
        Assert.Equal("reflection-v3", edge.ExtractorVersion);
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
    public async Task TypeGetType_Call_KeepsTypeCandidate()
    {
        var extraction = await ExtractAsync(One("""
                                                using System;
                                                namespace N;
                                                public class Target { }
                                                public class User
                                                {
                                                    public void Use() { var t = Type.GetType("Target"); }
                                                }
                                                """));

        var edge = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Target",
            Provenance.NameCandidate);
        AssertReflectionContract(edge, "ReflectionNameCandidate", Provenance.NameCandidate,
            extraction.ResolveId("global::N.User.Use"));
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

        var typeRef = extraction.SingleEdge("ReflectionTypeRef", "global::N.User.Use", "global::N.Target");
        Assert.Equal("compiler_proved", typeRef.Provenance);

        var memberRef = extraction.SingleEdge("ReflectionMemberRef", "global::N.User.Use", "global::N.Target.Name");
        Assert.Equal("compiler_proved", memberRef.Provenance);

        var nameCandidate = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Target.Run",
            Provenance.NameCandidate);
        Assert.Equal("reflection-v3", nameCandidate.ExtractorVersion);
    }

    [Fact]
    public async Task PropertyChangedLiteral_MatchingOwnMemberName_DoesNotProduceSelfEdge()
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

        // No ReflectionNameCandidate edge where source == target (self-reference).
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "ReflectionNameCandidate" && e.SourceSymbolId == e.TargetSymbolId);

        // Specifically, Login must not emit a candidate to itself despite the literal "Login" in its body.
        var loginId = extraction.ResolveId("global::N.AccessController.Login");
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "ReflectionNameCandidate" && e.Provenance == Provenance.NameCandidate
                 && e.SourceSymbolId == loginId && e.TargetSymbolId == loginId);

        var logoutId = extraction.ResolveId("global::N.AccessController.Logout");
        Assert.DoesNotContain(extraction.Result.Edges,
            e => e.Kind == "ReflectionNameCandidate" && e.Provenance == Provenance.NameCandidate
                 && e.SourceSymbolId == logoutId && e.TargetSymbolId == logoutId);
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
}