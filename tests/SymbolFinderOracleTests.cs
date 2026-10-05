using Lurp.Parity.Shared;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Text;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Tests;

/// <summary>
///     Oracle B dispatch-family acceptance: Roslyn's
///     <c>SymbolFinder.FindCallersAsync</c> reports a caller of an
///     interface/abstract member also as a caller of its implementation/override,
///     and the reverse. Lurp persists the direct call as a Calls edge to the bound
///     member plus a MayDispatchTo edge between the interface or root virtual
///     member and its implementation or override; the oracle must accept a caller
///     that matches any member of the target's dispatch family, and only through
///     such a persisted MayDispatchTo edge.
/// </summary>
public sealed class SymbolFinderOracleTests
{
    private static readonly MetadataReference[] _references =
        [.. ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
        .Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => MetadataReference.CreateFromFile(p!))];

    private const string InterfaceImplementationSource = """
        public interface I { void M(); }

        public class C : I { public void M() { } }

        public static class A
        {
            public static void CallI(I i) { i.M(); }
        }

        public static class B
        {
            public static void CallC(C c) { c.M(); }
        }
        """;

    private const string OverrideChainSource = """
        public abstract class Base { public abstract void M(); }

        public class Mid : Base { public override void M() { } }

        public class Leaf : Mid { public override void M() { } }

        public static class Caller
        {
            public static void Call(Base b) { b.M(); }
        }
        """;

    [Fact]
    public async Task TargetImplementation_CallerOfInterfaceMatchesThroughDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var aCallI = MethodId(compilation, "A", "CallI");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (aCallI, nameof(EdgeKind.Calls), im),
            (im, nameof(EdgeKind.MayDispatchTo), cm)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, cm);

        Assert.DoesNotContain(aCallI, missing);
    }

    [Fact]
    public async Task TargetImplementation_CallerOfInterfaceWithoutDispatchEdgeIsAMiss()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var aCallI = MethodId(compilation, "A", "CallI");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (aCallI, nameof(EdgeKind.Calls), im)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, cm);

        Assert.Contains(aCallI, missing);
    }

    [Fact]
    public async Task TargetInterface_CallerOfImplementationMatchesThroughDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var bCallC = MethodId(compilation, "B", "CallC");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (bCallC, nameof(EdgeKind.Calls), cm),
            (im, nameof(EdgeKind.MayDispatchTo), cm)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, im);

        Assert.DoesNotContain(bCallC, missing);
    }

    [Fact]
    public async Task TargetInterface_CallerOfImplementationWithoutDispatchEdgeIsAMiss()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var bCallC = MethodId(compilation, "B", "CallC");
        var im = MethodId(compilation, "I", "M");
        var cm = MethodId(compilation, "C", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (bCallC, nameof(EdgeKind.Calls), cm)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, im);

        Assert.Contains(bCallC, missing);
    }

    [Fact]
    public async Task TargetLastOverride_CallerOfBaseMatchesThroughTransitiveDispatchFamily()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, OverrideChainSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var caller = MethodId(compilation, "Caller", "Call");
        var baseM = MethodId(compilation, "Base", "M");
        var midM = MethodId(compilation, "Mid", "M");
        var leafM = MethodId(compilation, "Leaf", "M");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (caller, nameof(EdgeKind.Calls), baseM),
            (baseM, nameof(EdgeKind.MayDispatchTo), midM),
            (midM, nameof(EdgeKind.MayDispatchTo), leafM)
        };

        var missing = await MissingCallerIdsAsync(solution, edges, leafM);

        Assert.DoesNotContain(caller, missing);
    }

    [Fact]
    public async Task TargetInterfaceMember_ExternalInheritedImplementation_IsSkipped()
    {
        using var workspace = new AdhocWorkspace();
        var external = CompileExternalReference(
            "SymbolFinderOracleExternalFixture",
            """
            namespace External;

            public class Base { public void M() { } }
            """);
        var solution = CreateSolution(
            workspace,
            """
            public interface ISolution { void M(); }

            public class Derived : External.Base, ISolution { }
            """,
            [external]);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var isolutionM = MethodId(compilation, "ISolution", "M");

        var result = await RunAsync(solution, [], isolutionM);

        Assert.Contains(isolutionM, result.Summary.SkippedExternalDispatch);
        Assert.DoesNotContain(result.Targets, target => target.TargetId == isolutionM);
    }

    [Fact]
    public async Task TargetImplementation_ExternalInterfaceMember_IsSkipped()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(
            workspace,
            """
            public class C : System.IDisposable
            {
                public void Dispose() { }
            }
            """);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var dispose = MethodId(compilation, "C", "Dispose");

        var result = await RunAsync(solution, [], dispose);

        Assert.Contains(dispose, result.Summary.SkippedExternalDispatch);
        Assert.DoesNotContain(result.Targets, target => target.TargetId == dispose);
    }

    [Fact]
    public async Task TargetInternalImplementation_IsNotSkipped()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, InterfaceImplementationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var cm = MethodId(compilation, "C", "M");

        var result = await RunAsync(solution, [], cm);

        Assert.Empty(result.Summary.SkippedExternalDispatch);
        Assert.Contains(result.Targets, target => target.TargetId == cm);
    }

    private static async Task<SymbolFinderOracleResult> RunAsync(
        Solution solution,
        IReadOnlyList<(string Source, string Kind, string Target)> edges,
        string targetId)
    {
        return await SymbolFinderOracle.CompareAsync(
            solution,
            edges,
            OperationShapeOracle.NormalizedDocId,
            new HashSet<string>(StringComparer.Ordinal) { targetId });
    }

    private static PortableExecutableReference CompileExternalReference(string assemblyName, string source)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(source)],
            _references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
            throw new InvalidOperationException(
                "External fixture did not compile: " + string.Join("; ", emit.Diagnostics));
        return MetadataReference.CreateFromImage(stream.ToArray().ToImmutableArray());
    }

    private static async Task<IReadOnlyList<string>> MissingCallerIdsAsync(
        Solution solution,
        IReadOnlyList<(string Source, string Kind, string Target)> edges,
        string targetId)
    {
        var result = await SymbolFinderOracle.CompareAsync(
            solution,
            edges,
            OperationShapeOracle.NormalizedDocId,
            new HashSet<string>(StringComparer.Ordinal) { targetId });

        var target = Assert.Single(result.Targets);
        Assert.Equal(targetId, target.TargetId);
        return [.. target.MissingCallers.SelectMany(static missing => missing.CallerIds)];
    }

    private static string MethodId(Compilation compilation, string typeName, string methodName)
    {
        var type = compilation.Assembly.GlobalNamespace.GetTypeMembers(typeName).Single();
        var method = type.GetMembers(methodName).OfType<IMethodSymbol>().Single();
        return OperationShapeOracle.NormalizedDocId(method)
               ?? throw new InvalidOperationException($"No doc id for {typeName}.{methodName}.");
    }

    private static Solution CreateSolution(
        AdhocWorkspace workspace,
        string source,
        IEnumerable<MetadataReference>? extraReferences = null)
    {
        var solutionId = SolutionId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), "SymbolFinderOracleTests.slnx"));

        var references = new List<MetadataReference>(_references);
        if (extraReferences is not null)
            references.AddRange(extraReferences);

        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                "TestProject",
                "TestProject",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: references))
            .AddDocument(
                RoslynDocumentId.CreateNewId(projectId),
                "Source.cs",
                SourceText.From(source, Encoding.UTF8));

        if (!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not apply the test solution.");

        return workspace.CurrentSolution;
    }
}
