using Lurp.Shared;
using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
/// Phase 1 golden tests for the reflection extractors: typeof, nameof, and
/// string-literal name candidates. Pattern B (in-memory compilation).
/// </summary>
public sealed class GoldenReflectionTests : InMemoryTestBase
{
    private const string Doc = "Source.cs";

    private static IReadOnlyDictionary<string, string> One(string source)
        => new Dictionary<string, string> { [Doc] = source };

    private static void AssertReflectionContract(EdgeRecord edge, string kind, string provenance, string sourceFqn)
    {
        Assert.Equal(kind, edge.Kind);
        Assert.Equal(provenance, edge.Provenance);
        Assert.Equal("reflection-v1", edge.ExtractorVersion);
        Assert.NotNull(edge.SourceDocumentPath);
        Assert.EndsWith(Doc, edge.SourceDocumentPath);
        Assert.Equal(sourceFqn, edge.SourceSymbolId);
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
        AssertReflectionContract(edge, "ReflectionTypeRef", Provenance.CompilerProved, extraction.ResolveId("global::N.User.Use"));
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
        AssertReflectionContract(edge, "ReflectionMemberRef", Provenance.CompilerProved, extraction.ResolveId("global::N.User.Use"));
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
            r => r.Reason == "unsupported_syntax" && r.DocumentPath?.EndsWith(Doc) == true);

        // The reflection member-reference edge is still emitted for the nameof.
        var edge = extraction.SingleEdge("ReflectionMemberRef", "global::N.User.Use", "global::N.Target.Name");
        AssertReflectionContract(edge, "ReflectionMemberRef", Provenance.CompilerProved, extraction.ResolveId("global::N.User.Use"));
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
            r => r.DocumentPath?.EndsWith(Doc) == true && r.Reason == "compiler_error");
    }

    [Fact]
    public async Task ReflectionNameCandidate_StringLiteralMatchingSymbolName()
    {
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Target { }
            public class User
            {
                public void Use() { var name = "Target"; }
            }
            """));

        var edge = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Target", Provenance.NameCandidate);
        AssertReflectionContract(edge, "ReflectionNameCandidate", Provenance.NameCandidate, extraction.ResolveId("global::N.User.Use"));
    }

    [Fact]
    public async Task AllThreeReflectionKinds_FromOneSource()
    {
        var extraction = await ExtractAsync(One("""
            namespace N;
            public class Target
            {
                public string Name { get; set; }
            }
            public class User
            {
                public void Use()
                {
                    var t = typeof(Target);
                    var n = nameof(Target.Name);
                    var s = "Target";
                }
            }
            """));

        var typeRef = extraction.SingleEdge("ReflectionTypeRef", "global::N.User.Use", "global::N.Target");
        Assert.Equal("compiler_proved", typeRef.Provenance);

        var memberRef = extraction.SingleEdge("ReflectionMemberRef", "global::N.User.Use", "global::N.Target.Name");
        Assert.Equal("compiler_proved", memberRef.Provenance);

        var nameCandidate = extraction.SingleEdge("ReflectionNameCandidate", "global::N.User.Use", "global::N.Target", Provenance.NameCandidate);
        Assert.Equal("reflection-v1", nameCandidate.ExtractorVersion);
    }
}
