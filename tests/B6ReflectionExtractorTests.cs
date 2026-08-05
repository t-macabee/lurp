// Purpose: focused tests for the B6 reflection ladder (TypeOf/NameOf/StringLiteral/UnknownPattern).
// Owns: the B6ReflectionTests class covering reflection-target edge extraction.
// Must not contain: unrelated extractor/adapter tests, or storage/migration tests.

using Lurp.Adapters;
using Lurp.Shared;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using System.Text;
using DocumentId = Lurp.Workspace.DocumentId;

namespace Lurp.Storage.Tests;

public partial class MigrationRunnerTests
{
    public class B6ReflectionTests
    {
        private static Compilation CreateCompilation(string source, string path = "test.cs")
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: path);
            return CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        }

        [Fact]
        public void TypeOf_EmitsReflectionTypeRefEdge()
        {
            var source = @"
class Foo { }
class Bar {
    void M() { var t = typeof(Foo); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-typeof", "/");
            var edges = extractor.Extract();

            var reflectionEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTypeRef.ToString()).ToList();
            var edge = Assert.Single(reflectionEdges);
            Assert.Equal("compiler_proved", edge.Provenance);
            Assert.Contains("Foo", edge.TargetSymbolId);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void NameOf_EmitsReflectionMemberRefEdge()
        {
            var source = @"
class Foo {
    public void Bar() { }
}
class Baz {
    void M() { _ = nameof(Foo.Bar); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-nameof", "/");
            var edges = extractor.Extract();

            var reflectionEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionMemberRef.ToString()).ToList();
            var edge = Assert.Single(reflectionEdges);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void StringLiteral_MatchingTypeName_EmitsNameCandidateEdge()
        {
            var source = @"
class SomeKnownType { }
class Bar {
    void M() { var s = ""SomeKnownType""; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-stringlit", "/");
            var edges = extractor.Extract();

            var nameEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionNameCandidate.ToString()).ToList();
            var edge = Assert.Single(nameEdges);
            Assert.Equal("name_candidate", edge.Provenance);
            Assert.Contains("SomeKnownType", edge.TargetSymbolId);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void TypeGetType_EmitsUnknownEdge()
        {
            var source = @"
class Bar {
    void M() { var t = System.Type.GetType(""Something""); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-unknown", "/");
            var edges = extractor.Extract();

            var unknownEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTargetUnknown.ToString()).ToList();
            var edge = Assert.Single(unknownEdges);
            Assert.Equal("runtime_unknown", edge.Provenance);
            Assert.Contains("M", edge.SourceSymbolId);
        }

        [Fact]
        public void NoReflection_EmitsZeroEdges()
        {
            var source = @"
class Foo {
    void M() { int x = 42; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-none", "/");
            var edges = extractor.Extract();

            Assert.Empty(edges);
        }

        [Fact]
        public void NameOf_UnresolvableExpression_EmitsNoEdges()
        {
            var source = @"
class Bar {
    void M() { _ = nameof(UnknownType.UnknownMember); }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-nameof-unresolved", "/");
            var edges = extractor.Extract();

            Assert.Empty(edges);
        }

        [Fact]
        public void StringLiteral_MatchingMemberName_EmitsNameCandidateEdge()
        {
            var source = @"
class Foo {
    public void Bar() { }
}
class Baz {
    void M() { var s = ""Bar""; }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-stringlit-member", "/");
            var edges = extractor.Extract();

            var nameEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionNameCandidate.ToString()).ToList();
            var memberEdge = nameEdges.FirstOrDefault(e => e.TargetSymbolId.Contains("Bar"));
            Assert.NotNull(memberEdge);
            Assert.Equal("name_candidate", memberEdge.Provenance);
        }

        [Fact]
        public void MultipleReflectionPatterns_EmitsMultipleEdges()
        {
            var source = @"
class TargetType { }
class Source {
    void M() {
        var t = typeof(TargetType);
        _ = nameof(TargetType);
    }
}";
            var compilation = CreateCompilation(source);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-multi", "/");
            var edges = extractor.Extract();

            var typeRefEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTypeRef.ToString()).ToList();
            var memberRefEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionMemberRef.ToString()).ToList();

            Assert.NotEmpty(typeRefEdges);
            Assert.NotEmpty(memberRefEdges);
            Assert.True(edges.Count >= 2);
        }

        [Fact]
        public void ActivatorCreateInstance_EmitsReflectionTargetUnknownEdge()
        {
            var source = @"
class Target { }
class Source {
    void M() { var x = System.Activator.CreateInstance<Target>(); }
}";

            var systemRuntimePath = typeof(System.Activator).Assembly.Location;
            var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
            var compilation = CSharpCompilation.Create(
                "TestAssembly",
                [syntaxTree],
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                    MetadataReference.CreateFromFile(systemRuntimePath)
                ]);
            var extractor = new ReflectionExtractor(compilation, "snap-b6-activator", "/");
            var edges = extractor.Extract();

            var unknownEdges = edges.Where(e => e.Kind == EdgeKind.ReflectionTargetUnknown.ToString()).ToList();
            Assert.NotEmpty(unknownEdges);
            Assert.Contains(unknownEdges, e => e.Provenance == "runtime_unknown");
        }
    }
}
