using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Reflection;
using RoslynDocumentId = Microsoft.CodeAnalysis.DocumentId;

namespace Lurp.Tests;

/// <summary>
/// R2 regression: snapshot identity and freshness must distinguish two metadata
/// references that share an <see cref="AssemblyName.FullName"/> but differ in
/// content — the NuGet "patched contents, same version" scenario.
/// <c>WorkspaceInfo.TryGetAssemblyIdentity</c> folds a content hash into the
/// identity string, so <see cref="SnapshotIdentity"/> yields a different
/// <c>SnapshotId</c> and <see cref="WorkspaceFreshness"/> reports a
/// metadata-reference mismatch.
///
/// Before R2 these asserted the opposite (same id / no mismatch): identity was
/// derived from the full name alone, so a same-version patch was invisible and
/// Lurp could silently serve a graph built against different bytes.
/// </summary>
public sealed class AssemblyIdentityGranularityTests : IDisposable
{
    private static readonly MetadataReference[] _platformReferences =
        (((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")) ?? "")
        .Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => MetadataReference.CreateFromFile(p!))
        .ToArray();

    private readonly string _tempDir;

    public AssemblyIdentityGranularityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lurp-r2-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void SameAssemblyVersion_DifferentBytes_ProducesDifferentSnapshotId()
    {
        // Two builds of dependency "Dep" with identical assembly identity
        // (name + version) but different IL — the NuGet "patched contents, same
        // version" scenario.
        var depV1 = EmitDependency("v1", returnValue: 1);
        var depV2 = EmitDependency("v2", returnValue: 2);

        // Sanity: the assembly full names are identical, but the bytes are not.
        Assert.Equal(
            AssemblyName.GetAssemblyName(depV1).FullName,
            AssemblyName.GetAssemblyName(depV2).FullName);
        Assert.NotEqual(File.ReadAllBytes(depV1), File.ReadAllBytes(depV2));

        var workspaceV1 = BuildConsumerWorkspace(depV1);
        var workspaceV2 = BuildConsumerWorkspace(depV2);

        var idV1 = SnapshotIdentity.Create(workspaceV1, new HashSet<string>());
        var idV2 = SnapshotIdentity.Create(workspaceV2, new HashSet<string>());

        // Identity now folds a content hash, so the same-version patch is
        // distinguished. (Before R2 this was Assert.Equal.)
        Assert.NotEqual(idV1, idV2);
    }

    [Fact]
    public void SameAssemblyVersion_DifferentBytes_FreshnessReportsMismatch()
    {
        var depV1 = EmitDependency("v1", returnValue: 1);
        var depV2 = EmitDependency("v2", returnValue: 2);

        var workspaceV1 = BuildConsumerWorkspace(depV1);
        var workspaceV2 = BuildConsumerWorkspace(depV2);

        var storedV1 = SnapshotManifest.FromWorkspace(
            workspaceV1, SnapshotIdentity.Create(workspaceV1, new HashSet<string>()));

        var mismatches = WorkspaceFreshness.GetFullRebuildMismatches(workspaceV2, storedV1);

        // The freshness gate now detects the metadata-reference content change.
        // (Before R2 this was Assert.DoesNotContain.)
        Assert.Contains(mismatches, m => m.Kind == MismatchKind.MetadataReferencesChanged);
    }

    /// <summary>
    /// Emits a "Dep" assembly (name + version fixed) whose single method returns
    /// <paramref name="returnValue"/>, guaranteeing different IL between builds
    /// while keeping the assembly identity constant. Written to a per-build
    /// subdirectory under a fixed file name, mirroring a package cache whose
    /// same-named DLL was overwritten by a patch.
    /// </summary>
    private string EmitDependency(string buildTag, int returnValue)
    {
        var source = $$"""
            [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

            namespace Dep
            {
                public static class Api
                {
                    public static int Value() => {{returnValue}};
                }
            }
            """;

        var compilation = CSharpCompilation.Create(
            assemblyName: "Dep",
            syntaxTrees: new[] { CSharpSyntaxTree.ParseText(source) },
            references: _platformReferences,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var buildDir = Path.Combine(_tempDir, buildTag);
        Directory.CreateDirectory(buildDir);
        var outputPath = Path.Combine(buildDir, "Dep.dll");

        var emit = compilation.Emit(outputPath);
        if (!emit.Success)
        {
            throw new InvalidOperationException(
                "Failed to emit Dep.dll: " +
                string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return outputPath;
    }

    /// <summary>
    /// Builds a single-project workspace referencing the given dependency DLL on
    /// top of the platform references, then wraps it in a <see cref="WorkspaceInfo"/>.
    /// </summary>
    private WorkspaceInfo BuildConsumerWorkspace(string dependencyPath)
    {
        using var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solutionId = SolutionId.CreateNewId();

        workspace.AddSolution(SolutionInfo.Create(
            solutionId, VersionStamp.Create(), filePath: Path.Combine(_tempDir, "Consumer.slnx")));

        var references = _platformReferences
            .Append(MetadataReference.CreateFromFile(dependencyPath))
            .ToArray();

        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name: "P",
                assemblyName: "P",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable),
                metadataReferences: references));

        const string consumerSource = """
            namespace P;

            public class Svc
            {
                public int Value() => Dep.Api.Value();
            }
            """;
        var docPath = Path.Combine(_tempDir, "Svc.cs");
        File.WriteAllText(docPath, consumerSource);
        solution = solution.AddDocument(
            RoslynDocumentId.CreateNewId(projectId),
            "Svc.cs",
            SourceText.From(consumerSource, System.Text.Encoding.UTF8),
            filePath: docPath);

        workspace.TryApplyChanges(solution);

        return new WorkspaceInfo(workspace.CurrentSolution, _tempDir);
    }
}
