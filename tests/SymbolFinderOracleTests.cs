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

    private const string MethodGroupArgumentSource = """
        public static class Caller
        {
            public static void RunTarget() { System.Threading.Tasks.Task.Run(Target); }

            public static void Target() { }
        }
        """;

    private const string DelegateCreationSource = """
        public static class Caller
        {
            public static void RunTarget() { var a = new System.Action(Target); }

            public static void Target() { }
        }
        """;

    private const string LocalMethodGroupArgumentSource = """
        public static class Local
        {
            public static void Take(System.Action a) { }
        }

        public static class Caller
        {
            public static void RunTarget() { Local.Take(Target); }

            public static void Target() { }
        }
        """;

    private const string UsingExternalInterfaceSource = """
        public sealed class Stub : System.ComponentModel.IComponent
        {
            public System.ComponentModel.ISite Site { get; set; }
            public event System.EventHandler Disposed;
            public void Dispose() { }
        }

        public static class Caller
        {
            public static System.ComponentModel.IComponent Make() => new Stub();
            public static void UseIt() { using var s = Make(); }
        }
        """;

    private const string UsingTargetTypeSource = """
        public sealed class Stub : System.ComponentModel.IComponent
        {
            public System.ComponentModel.ISite Site { get; set; }
            public event System.EventHandler Disposed;
            public void Dispose() { }
        }

        public static class Caller
        {
            public static void UseIt() { using var s = new Stub(); }
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
    public async Task TargetInterfaceMember_ExternalInheritedImplementation_ConcreteCallerIsSetAside()
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

            public static class Callers
            {
                public static void CallDerived(Derived d) { d.M(); }
                public static void CallInterface(ISolution s) { s.M(); }
            }
            """,
            [external]);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var isolutionM = MethodId(compilation, "ISolution", "M");
        var callDerived = MethodId(compilation, "Callers", "CallDerived");
        var callInterface = MethodId(compilation, "Callers", "CallInterface");

        var result = await SymbolFinderOracle.CompareAsync(
            solution,
            [(callInterface, nameof(EdgeKind.Calls), isolutionM)],
            OperationShapeOracle.NormalizedDocId,
            new HashSet<string>(StringComparer.Ordinal) { isolutionM });

        var target = Assert.Single(result.Targets);
        Assert.Equal(isolutionM, target.TargetId);
        Assert.DoesNotContain(callInterface, target.MissingCallers.SelectMany(static missing => missing.CallerIds));

        // The concrete caller binds to External.Base.M, which is outside the
        // solution: it is set aside, not counted as a miss.
        var setAside = Assert.Single(result.Summary.SetAsideCallers);
        Assert.Equal(SymbolFinderOracle.BindsExternalReason, setAside.Reason);
        Assert.Contains(callDerived, setAside.CallerIds);
    }

    [Fact]
    public async Task TargetImplementation_ExternalInterfaceMember_IsCheckedNotSkipped()
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

        // The external implementation no longer hides the target; with no
        // callers it is checked and produces no miss.
        Assert.Contains(result.Targets, target => target.TargetId == dispose);
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetDispose_UsingOfExternalInterface_IsSetAsideAsBindsExternal()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, UsingExternalInterfaceSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var dispose = MethodId(compilation, "Stub", "Dispose");
        var useIt = MethodId(compilation, "Caller", "UseIt");

        var result = await RunAsync(solution, [], dispose);

        // The using binds IComponent.Dispose through the interface return type,
        // which is outside the solution: set aside, not a miss on Stub.Dispose.
        var target = Assert.Single(result.Targets);
        Assert.Equal(dispose, target.TargetId);
        Assert.DoesNotContain(useIt, target.MissingCallers.SelectMany(static missing => missing.CallerIds));

        var setAside = Assert.Single(result.Summary.SetAsideCallers);
        Assert.Equal(SymbolFinderOracle.BindsExternalReason, setAside.Reason);
        Assert.Contains(useIt, setAside.CallerIds);
    }

    [Fact]
    public async Task TargetDispose_UsingOfTargetType_IsExpected()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, UsingTargetTypeSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var dispose = MethodId(compilation, "Stub", "Dispose");
        var useIt = MethodId(compilation, "Caller", "UseIt");

        var result = await RunAsync(solution, [], dispose);

        // The using binds Stub.Dispose directly: expected, and with no edge it
        // stays a miss.
        var target = Assert.Single(result.Targets);
        Assert.Equal(dispose, target.TargetId);
        Assert.Contains(useIt, target.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.DoesNotContain(result.Summary.SetAsideCallers, entry => entry.CallerIds.Contains(useIt));
    }

    [Fact]
    public async Task TargetImplementation_MetadataInterfaceFromSecondProject_CallerThroughInterfaceIsExpected()
    {
        using var workspace = new AdhocWorkspace();
        var interfaceImage = CompileExternalReference(
            "SymbolFinderOracleMetadataInterface",
            "public interface I { void M(); }");
        var solution = CreateTwoProjectSolution(
            workspace,
            "public interface I { void M(); }",
            """
            public class C : I { public void M() { } }

            public static class BCaller
            {
                public static void Call(I i) { i.M(); }
            }
            """,
            interfaceImage);
        var projectB = solution.Projects.Single(project => project.Name == "ProjectB");
        var compilationB = await projectB.GetCompilationAsync()
                           ?? throw new InvalidOperationException("Compilation not loaded.");
        var cm = MethodId(compilationB, "C", "M");
        var bCaller = MethodId(compilationB, "BCaller", "Call");

        // The interface member as project B sees it: a metadata view from the
        // first project's image, whose containing-assembly object differs from
        // the source project's assembly.
        var metadataI = compilationB.GetTypeByMetadataName("I")
                        ?? throw new InvalidOperationException("Metadata interface not found.");
        var metadataIM = metadataI.GetMembers("M").OfType<IMethodSymbol>().Single();
        var metadataIMId = OperationShapeOracle.NormalizedDocId(metadataIM)
                           ?? throw new InvalidOperationException("No doc id for metadata I.M.");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (bCaller, nameof(EdgeKind.Calls), metadataIMId),
            (metadataIMId, nameof(EdgeKind.MayDispatchTo), cm)
        };

        var result = await SymbolFinderOracle.CompareAsync(
            solution,
            edges,
            OperationShapeOracle.NormalizedDocId,
            new HashSet<string>(StringComparer.Ordinal) { cm });

        var target = Assert.Single(result.Targets);
        Assert.Equal(cm, target.TargetId);
        Assert.Empty(target.MissingCallers);
        Assert.DoesNotContain(result.Summary.SetAsideCallers,
            entry => entry.CallerIds.Contains(bCaller));
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

        // C.M is in the solution, so it is checked; the interface caller binds to
        // I.M, which is in solution but outside C.M's dispatch family {C.M} and is
        // recorded as binds_other (still checked, so it stays a miss).
        Assert.DoesNotContain(result.Summary.SetAsideCallers,
            entry => entry.Reason == SymbolFinderOracle.BindsExternalReason);
        Assert.Contains(result.Targets, target => target.TargetId == cm);
    }

    [Fact]
    public async Task TargetMethodGroup_PassedToExternalCall_MatchesThroughMethodGroupRefEdge()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, MethodGroupArgumentSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var edges = new (string Source, string Kind, string Target)[]
        {
            (runTarget, nameof(EdgeKind.MethodGroupRef), target)
        };

        var result = await RunAsync(solution, edges, target);

        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(target, targetResult.TargetId);
        Assert.DoesNotContain(runTarget, targetResult.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetMethodGroup_PassedToExternalCallWithoutEdge_IsAMissNotSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, MethodGroupArgumentSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var result = await RunAsync(solution, [], target);

        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(target, targetResult.TargetId);
        Assert.Contains(runTarget, targetResult.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetMethodGroup_InDelegateCreationWithoutEdge_IsAMissNotSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, DelegateCreationSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var result = await RunAsync(solution, [], target);

        var targetResult = Assert.Single(result.Targets);
        Assert.Equal(target, targetResult.TargetId);
        Assert.Contains(runTarget, targetResult.MissingCallers.SelectMany(static missing => missing.CallerIds));
        Assert.Empty(result.Summary.SetAsideCallers);
    }

    [Fact]
    public async Task TargetMethodGroup_PassedToLocalCall_IsNotSetAside()
    {
        using var workspace = new AdhocWorkspace();
        var solution = CreateSolution(workspace, LocalMethodGroupArgumentSource);
        var compilation = await solution.Projects.Single().GetCompilationAsync()
                          ?? throw new InvalidOperationException("Compilation not loaded.");
        var runTarget = MethodId(compilation, "Caller", "RunTarget");
        var target = MethodId(compilation, "Caller", "Target");

        var result = await RunAsync(solution, [], target);

        Assert.DoesNotContain(result.Summary.SetAsideCallers, entry => entry.CallerIds.Contains(runTarget));
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

    private static Solution CreateTwoProjectSolution(
        AdhocWorkspace workspace,
        string sourceA,
        string sourceB,
        MetadataReference referenceToA)
    {
        var solutionId = SolutionId.CreateNewId();
        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), "SymbolFinderOracleTests.slnx"));

        var projectAId = ProjectId.CreateNewId();
        var projectBId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectAId,
                VersionStamp.Create(),
                "ProjectA",
                "ProjectA",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: _references))
            .AddProject(ProjectInfo.Create(
                projectBId,
                VersionStamp.Create(),
                "ProjectB",
                "ProjectB",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [.. _references, referenceToA]))
            .AddDocument(RoslynDocumentId.CreateNewId(projectAId), "A.cs", SourceText.From(sourceA, Encoding.UTF8))
            .AddDocument(RoslynDocumentId.CreateNewId(projectBId), "B.cs", SourceText.From(sourceB, Encoding.UTF8));

        if (!workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("Could not apply the test solution.");

        return workspace.CurrentSolution;
    }
}
