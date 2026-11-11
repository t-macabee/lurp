using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Tests;

/// <summary>
///     S01-F31: the reflection stage uses the one scope predicate
///     (<c>ExtractionUtils.IsInScope</c>): a syntax tree without a file path has no
///     document to scope by and stays in scope.
/// </summary>
public sealed class ReflectionExtractorScopeTests
{
    [Fact]
    public void TreeWithoutPath_UnderAScope_StaysInScope()
    {
        var tree = CSharpSyntaxTree.ParseText("class C { void M() { var t = typeof(C); } }");
        var compilation = CSharpCompilation.Create("P", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var inputs = new ProjectExtractionInputs(
            compilation, "snap", new EdgeLocationResolver([], [], Path.GetTempPath()),
            new HashSet<string> { "/repo/Other.cs" }, null, []);

        var edges = new ReflectionExtractor(inputs).Extract();

        Assert.Single(edges, static e => e.Kind == "ReflectionTypeRef");
    }
}
