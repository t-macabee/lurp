using Lurp.Shared;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Storage.Tests;

public sealed class EdgeLocationResolverTests
{
    [Fact]
    public void Resolve_PreservesKnownRelativeDocumentPathAndSpan()
    {
        var resolver = new EdgeLocationResolver(
            ["src/Feature.cs"],
            Array.Empty<string>(),
            Path.GetTempPath());
        var tree = CSharpSyntaxTree.ParseText(
            "class Feature { }",
            path: Path.Combine(Path.GetTempPath(), "src", "Feature.cs"));

        var location = tree.GetRoot().GetLocation();
        var result = resolver.Resolve(location);

        Assert.Equal("src/Feature.cs", result.path);
        Assert.Equal(0, result.sl);
        Assert.Equal(0, result.sc);
        Assert.Equal(0, result.el);
        Assert.Equal(17, result.ec);
    }

    [Fact]
    public void Resolve_PreservesRelativeSyntaxPath()
    {
        var resolver = new EdgeLocationResolver(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Path.GetTempPath());
        var tree = CSharpSyntaxTree.ParseText("class Feature { }", path: @"tests\Feature.cs");

        var result = resolver.Resolve(tree.GetRoot().GetLocation());

        Assert.Equal("tests/Feature.cs", result.path);
    }

    [Fact]
    public void Resolve_MakesUnmappedAbsolutePathRelativeToExplicitRoot()
    {
        var gitRoot = Path.Combine(Path.GetTempPath(), "lurp-location-root");
        var absolutePath = Path.Combine(gitRoot, "src", "Feature.cs");
        var resolver = new EdgeLocationResolver(
            Array.Empty<string>(),
            Array.Empty<string>(),
            gitRoot);
        var tree = CSharpSyntaxTree.ParseText("class Feature { }", path: absolutePath);

        var result = resolver.Resolve(tree.GetRoot().GetLocation());

        Assert.Equal("src/Feature.cs", result.path);
    }

    [Fact]
    public void IsGenerated_PreservesExplicitAndConventionalDetection()
    {
        var resolver = new EdgeLocationResolver(
            Array.Empty<string>(),
            ["src/Generated.cs"],
            Path.GetTempPath());

        Assert.True(resolver.IsGenerated("src/Generated.cs"));
        Assert.True(resolver.IsGenerated("src/Feature.g.cs"));
        Assert.True(resolver.IsGenerated("src/generated/Feature.cs"));
        Assert.False(resolver.IsGenerated("src/Feature.cs"));
    }
}
