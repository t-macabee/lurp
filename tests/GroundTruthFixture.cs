using System.Diagnostics;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Tests;

/// <summary>
///     Loads a ground-truth corpus once (copied to temp, restored) and runs the
///     real extraction pipeline over every project compilation in-process, so the
///     ground-truth tests share one MSBuild load. Facts keep the full symbol id
///     (docCommentId|assembly) plus a doc-comment-id projection for comparison
///     with the reviewed golden file. Derived fixtures select the corpus;
///     multi-target projects contribute one compilation per target framework.
/// </summary>
public abstract class GroundTruthFixtureBase : IAsyncLifetime
{
    private string? _testDir;

    protected abstract string SolutionRelativePath { get; }

    public string SolutionPath { get; private set; } = "";
    public List<ExtractedFact> Facts { get; } = [];
    public List<(string Project, Compilation Compilation)> Compilations { get; } = [];
    public Solution? Solution { get; private set; }
    public int DeclaredCount { get; private set; }
    public IReadOnlyList<string> DeclaredSample { get; private set; } = [];
    public IReadOnlyList<string> FactSample { get; private set; } = [];

    public async Task InitializeAsync()
    {
        if (!TryRegisterMSBuild())
            throw new InvalidOperationException("MSBuildLocator could not register an SDK; ground-truth tests need one.");

        var fixtureRoot = LocateFixtureRoot();
        _testDir = Path.Combine(Path.GetTempPath(), $"lurp-groundtruth-{Guid.NewGuid():N}");
        var treeRoot = Path.Combine(_testDir, "GroundTruth");
        CopyTree(fixtureRoot, treeRoot);

        SolutionPath = Path.Combine(treeRoot, SolutionRelativePath);
        if (!File.Exists(SolutionPath))
            throw new InvalidOperationException($"Copied fixture solution missing: {SolutionPath}");

        RestoreSolution(SolutionPath);

        var workspace = MSBuildWorkspace.Create(LurpCache.CreateWorkspaceGlobalProperties(SolutionPath));
        Solution = await workspace.OpenSolutionAsync(SolutionPath);
        var workspaceInfo = new WorkspaceInfo(Solution, treeRoot);

        var declared = new HashSet<string>(StringComparer.Ordinal);
        var perProject = new List<(Compilation Compilation, List<EdgeRecord> Edges, List<SymbolDeclaration> Declarations)>();

        foreach (var project in Solution.Projects)
        {
            var compilation = await project.GetCompilationAsync()
                              ?? throw new InvalidOperationException($"GetCompilationAsync returned null for '{project.Name}'.");
            Compilations.Add((project.Name, compilation));

            var options = CompilationFactExtractor.CreateOptions();
            var result = CompilationFactExtractor.ExtractAll(compilation, workspaceInfo, GroundTruthSnapshotId, project.Name, project, options);
            result.EnsureRequiredSuccess();
            perProject.Add((compilation, result.Edges, result.Declarations));

            foreach (var declaration in result.Declarations)
                declared.Add(declaration.SymbolId.DocCommentId);
        }

        foreach (var (compilation, edges, _) in perProject)
        {
            var assembly = compilation.Assembly.Identity.GetDisplayName();
            foreach (var edge in edges)
            {
                var source = edge.SourceSymbolId;
                var target = edge.TargetSymbolId;
                var sourceParsed = SymbolId.TryParse(source, out var parsedSource);
                var targetParsed = SymbolId.TryParse(target, out var parsedTarget);
                var sourceDoc = sourceParsed ? parsedSource!.DocCommentId : source;
                var targetDoc = targetParsed ? parsedTarget!.DocCommentId : target;
                Facts.Add(new ExtractedFact(
                    sourceDoc,
                    edge.Kind,
                    targetDoc,
                    edge.ExtractorVersion,
                    source,
                    target,
                    sourceParsed && declared.Contains(sourceDoc),
                    targetParsed && declared.Contains(targetDoc),
                    assembly));
            }
        }

        DeclaredCount = declared.Count;
        DeclaredSample = [.. declared.Take(5)];
        FactSample = [.. Facts.Take(5).Select(f => $"{f.Source}|{f.Kind}|{f.Target}")];
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_testDir != null && Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch
        {
            // Best effort; the temp directory is outside the repo.
        }

        return Task.CompletedTask;
    }

    public sealed record ExtractedFact(
        string Source,
        string Kind,
        string Target,
        string ExtractorVersion,
        string SourceSymbolId,
        string TargetSymbolId,
        bool SourceDeclared,
        bool TargetDeclared,
        string Assembly);

    public const string GroundTruthSnapshotId = "00000000000000000000000000000001";

    public static string LocateFixtureRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "Fixtures", "GroundTruth", "CallShapes", "CallShapes.slnx");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(Path.GetDirectoryName(candidate))!;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate tests/Fixtures/GroundTruth from " + AppContext.BaseDirectory);
    }

    public static IReadOnlyList<GoldenFact> LoadGolden()
    {
        var path = Path.Combine(LocateFixtureRoot(), "CallShapes", "call-shapes.golden.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var facts = new List<GoldenFact>();
        foreach (var element in document.RootElement.GetProperty("facts").EnumerateArray())
        {
            facts.Add(new GoldenFact(
                element.GetProperty("id").GetString()!,
                element.GetProperty("source").GetString()!,
                element.GetProperty("kind").GetString()!,
                element.GetProperty("target").GetString()!,
                element.GetProperty("reason").GetString()!));
        }

        return facts;
    }

    public static bool TryRegisterMSBuild()
    {
        if (MSBuildLocator.IsRegistered)
            return true;
        try
        {
            MSBuildLocator.RegisterDefaults();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void RestoreSolution(string solutionPath)
    {
        var restore = RunProcess("dotnet", ["restore", solutionPath, "--nologo"], TimeSpan.FromMinutes(3));
        if (restore.ExitCode != 0)
            throw new InvalidOperationException($"dotnet restore failed ({restore.ExitCode}):\n{restore.Stderr}");
    }

    internal static void CopyTree(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, dir);
            if (IsBuildDirectory(rel))
                continue;
            Directory.CreateDirectory(Path.Combine(destinationRoot, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file);
            if (IsBuildDirectory(Path.GetDirectoryName(rel) ?? ""))
                continue;
            var destination = Path.Combine(destinationRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        static bool IsBuildDirectory(string relativeDir) =>
            relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(string fileName, string[] args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // A reused MSBuild worker node inherits the child's redirected pipe
        // handles and keeps them open after `dotnet restore` exits, which blocks
        // ReadToEnd below until the node idles out (~15 minutes). Disable node
        // reuse for fixture restores so the streams close with the process.
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{fileName} {string.Join(' ', args)} did not exit within {timeout}.");
        }

        Task.WaitAll(stdout, stderr);
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>Fixture 1: the CallShapes call-shape corpus (single target framework).</summary>
public sealed class GroundTruthFixture : GroundTruthFixtureBase
{
    protected override string SolutionRelativePath => Path.Combine("CallShapes", "CallShapes.slnx");
}

/// <summary>
///     Fixture 2: the C#-only CrossProject corpus. CrossProject.Lib multi-targets
///     net9.0/net10.0, so the pipeline extracts one compilation per target
///     framework and the fact set is the union over every TFM compilation.
/// </summary>
public sealed class CrossProjectGroundTruthFixture : GroundTruthFixtureBase
{
    protected override string SolutionRelativePath => Path.Combine("CrossProject", "CrossProject.CSharp.slnx");
}

public sealed record GoldenFact(string Id, string Source, string Kind, string Target, string Reason);

[CollectionDefinition("GroundTruth", DisableParallelization = true)]
public sealed class GroundTruthSuite
    : ICollectionFixture<GroundTruthFixture>, ICollectionFixture<CrossProjectGroundTruthFixture>;
