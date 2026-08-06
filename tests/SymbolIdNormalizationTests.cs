// Purpose: focused tests for SymbolIdFactory identity normalization — constructed
// generics and reduced extension methods must produce the same ID as their original
// definition, because only definitions are snapshot members and any other ID is
// silently removed by DeleteOrphanEdges.
// Owns: the SymbolIdNormalizationTests class and its Roslyn compilation helpers.

using Lurp.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Lurp.Storage.Tests;

public sealed class SymbolIdNormalizationTests
{
    private const string AmbientIdentity = "ambient-assembly";

    private static Compilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: "test.cs");
        return CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [Fact]
    public void Make_OrdinaryDefinition_IsUnchanged()
    {
        var compilation = CreateCompilation("class Foo { void Bar() {} }");
        var foo = compilation.GetTypeByMetadataName("Foo")!;

        var id = SymbolIdFactory.Make(foo, AmbientIdentity);

        Assert.NotNull(id);
        Assert.StartsWith("T:Foo|", id);
    }

    /// <summary>
    /// Regression: an edge to a constructed generic base (Derived : Base&lt;int&gt;)
    /// carried an instantiated ID (T:Base{System.Int32}) that matches no declared
    /// symbol, so the Inherits edge was orphan-deleted from every snapshot.
    /// </summary>
    [Fact]
    public void Make_ConstructedGenericBaseType_NormalizesToOpenDefinition()
    {
        var compilation = CreateCompilation(@"
class Base<T> { }
class Derived : Base<int> { }");
        var derived = compilation.GetTypeByMetadataName("Derived")!;
        var constructedBase = derived.BaseType!;
        Assert.False(constructedBase.IsDefinition);

        var constructedId = SymbolIdFactory.Make(constructedBase, AmbientIdentity);
        var definitionId = SymbolIdFactory.Make(constructedBase.OriginalDefinition, AmbientIdentity);

        Assert.NotNull(constructedId);
        Assert.Equal(definitionId, constructedId);
        Assert.StartsWith("T:Base`1|", constructedId);
        Assert.DoesNotContain("{", constructedId);
    }

    /// <summary>
    /// Regression: call sites bind extension methods in reduced form (receiver
    /// parameter removed), whose doc-comment ID omits the first parameter and so
    /// never matches the declared method — Calls/ExtensionReceiver edges to
    /// user-written extension methods were orphan-deleted from every snapshot.
    /// </summary>
    [Fact]
    public void Make_ReducedExtensionMethod_NormalizesToUnreducedDefinition()
    {
        var compilation = CreateCompilation(@"
static class Ext { public static int Twice(this int value) => value * 2; }
class User { int Use() => 3.Twice(); }");
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var reduced = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.NotNull(reduced.ReducedFrom);

        var reducedId = SymbolIdFactory.Make(reduced, AmbientIdentity);
        var declaredId = SymbolIdFactory.Make(
            compilation.GetTypeByMetadataName("Ext")!.GetMembers("Twice").Single(), AmbientIdentity);

        Assert.NotNull(reducedId);
        Assert.Equal(declaredId, reducedId);
        // The receiver parameter is present in the normalized ID.
        Assert.Contains("Twice(System.Int32)", reducedId);
    }

    /// <summary>
    /// A constructed generic *method* instantiation must also collapse onto its
    /// definition, for the same snapshot-membership reason as constructed types.
    /// </summary>
    [Fact]
    public void Make_ConstructedGenericMethod_NormalizesToDefinition()
    {
        var compilation = CreateCompilation(@"
class Foo
{
    static T Identity<T>(T value) => value;
    static int Use() => Identity(42);
}");
        var tree = compilation.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Single();
        var constructed = (IMethodSymbol)model.GetSymbolInfo(invocation).Symbol!;
        Assert.False(constructed.IsDefinition);

        var constructedId = SymbolIdFactory.Make(constructed, AmbientIdentity);
        var definitionId = SymbolIdFactory.Make(constructed.OriginalDefinition, AmbientIdentity);

        Assert.NotNull(constructedId);
        Assert.Equal(definitionId, constructedId);
    }
}
