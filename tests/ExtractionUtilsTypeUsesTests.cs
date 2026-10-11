using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Lurp.Tests;

/// <summary>
///     Unit tests of <see cref="ExtractionUtils.TypeUses" /> on pointer and function-pointer
///     types. A compilation with unsafe code cannot go through <c>One(...)</c>
///     (<see cref="InMemoryTestBase" /> does not allow it), so these build it directly.
/// </summary>
public sealed class ExtractionUtilsTypeUsesTests
{
    private static readonly MetadataReference[] _platformReferences =
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => MetadataReference.CreateFromFile(p!))];

    private const string Source = """
                                  namespace N;
                                  public struct P { public int V; }
                                  public unsafe class Host
                                  {
                                      public static delegate*<P, void> F;
                                      public static P* G;
                                  }
                                  """;

    [Fact]
    public void FunctionPointerReturningVoid_YieldsOnlyItsParameterTypes()
    {
        var compilation = BuildCompilation();
        var field = FieldOf(compilation, "F");

        // B61: void is not a type use, so the only type named by F is N.P.
        var uses = ExtractionUtils.TypeUses(field.Type).Select(t => t.ToDisplayString()).ToArray();
        Assert.Equal("N.P", Assert.Single(uses));
    }

    [Fact]
    public void PointerType_YieldsItsPointedAtType()
    {
        var compilation = BuildCompilation();
        var field = FieldOf(compilation, "G");

        var uses = ExtractionUtils.TypeUses(field.Type).Select(t => t.ToDisplayString()).ToArray();
        Assert.Equal("N.P", Assert.Single(uses));
    }

    private static CSharpCompilation BuildCompilation()
    {
        var compilation = CSharpCompilation.Create(
            "TypeUsesProbe",
            [CSharpSyntaxTree.ParseText(Source)],
            _platformReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.Empty(errors);
        return compilation;
    }

    private static IFieldSymbol FieldOf(CSharpCompilation compilation, string name)
    {
        var host = compilation.GetTypeByMetadataName("N.Host")!;
        return host.GetMembers(name).OfType<IFieldSymbol>().Single();
    }
}
