using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Tests;

/// <summary>
///     Audit B6 / Q12b: a non-C# project in the solution is a declared boundary
///     (<c>non_csharp_projects</c>), not a full-index failure. The
///     <c>NonCSharp.slnx</c> fixture (one C# project + one VB project) is indexed
///     by the real CLI in a child process, so MSBuild nodes die with the process
///     and a stuck load fails by timeout instead of hanging the test host.
/// </summary>
[Trait("Category", "Slow")]
public sealed class CrossProjectBoundaryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [Fact]
    public void VbProject_IsSkippedWithBoundaryWarning()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"lurp-nonCSharp-{Guid.NewGuid():N}");
        var treeRoot = Path.Combine(testDir, "GroundTruth");
        string? cacheDir = null;

        try
        {
            GroundTruthFixture.CopyTree(GroundTruthFixture.LocateFixtureRoot(), treeRoot);
            var solutionPath = Path.Combine(treeRoot, "NonCSharp", "NonCSharp.slnx");
            Assert.True(File.Exists(solutionPath), $"NonCSharp solution missing: {solutionPath}");

            GroundTruthFixture.RestoreSolution(solutionPath);
            cacheDir = Lurp.Workspace.LurpCache.ResolveSolutionCacheDir(solutionPath);

            var index = TargetTreeIntegrityTests.RunCli(Timeout, "--mode=index", $"--solution={solutionPath}", "--strategy=full");
            var output = index.Stdout + index.Stderr;

            Assert.True(index.ExitCode == 0, $"full index exited {index.ExitCode}:\n{output}");
            Assert.Contains("Index complete for snapshot", index.Stdout);
            Assert.Contains("VbLib", output);
            Assert.Contains("non_csharp_projects", output);
        }
        finally
        {
            try { if (cacheDir != null && Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }

    [SkippableFact]
    public async Task MultiTargetSolution_LoadsWithoutDuplicateOutputPathDiagnostic_AndBindsMatchingFlavor()
    {
        Skip.If(!GroundTruthFixture.TryRegisterMSBuild(), "MSBuild is not available on this system.");

        var testDir = Path.Combine(Path.GetTempPath(), $"lurp-multitfm-{Guid.NewGuid():N}");
        var treeRoot = Path.Combine(testDir, "GroundTruth");
        string? cacheDir = null;

        try
        {
            GroundTruthFixture.CopyTree(GroundTruthFixture.LocateFixtureRoot(), treeRoot);
            var solutionPath = Path.Combine(treeRoot, "CrossProject", "CrossProject.CSharp.slnx");
            Assert.True(File.Exists(solutionPath), $"C#-only CrossProject solution missing: {solutionPath}");

            GroundTruthFixture.RestoreSolution(solutionPath);
            cacheDir = LurpCache.ResolveSolutionCacheDir(solutionPath);

            using var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(solutionPath));
            var solution = await workspace.OpenSolutionAsync(solutionPath);
            var diagnostics = workspace.Diagnostics.ToList();

            var libNet10 = solution.Projects.Single(p =>
                p.Name.StartsWith("CrossProject.Lib", StringComparison.Ordinal)
                && p.ParseOptions is CSharpParseOptions parse
                && parse.PreprocessorSymbolNames.Contains("NET10_0"));
            var libNet9 = solution.Projects.Single(p =>
                p.Name.StartsWith("CrossProject.Lib", StringComparison.Ordinal)
                && p.ParseOptions is CSharpParseOptions parse
                && parse.PreprocessorSymbolNames.Contains("NET9_0")
                && !parse.PreprocessorSymbolNames.Contains("NET10_0"));
            var app = solution.Projects.Single(p => p.Name.StartsWith("CrossProject.App", StringComparison.Ordinal));

            var referencedIds = app.ProjectReferences.Select(r => r.ProjectId).ToList();
            Assert.Contains(libNet10.Id, referencedIds);
            Assert.DoesNotContain(libNet9.Id, referencedIds);

            Assert.DoesNotContain(diagnostics,
                d => d.Message.Contains("same file path and output path", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { if (cacheDir != null && Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }
}
