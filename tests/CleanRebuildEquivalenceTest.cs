using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace Lurp.Tests;

public sealed class CleanRebuildEquivalenceTest : IDisposable
{
    private readonly string _testDir;
    private readonly string _solutionPath;
    private readonly string _dbPath;
    private readonly string _cleanRebuildDbPath;

    static CleanRebuildEquivalenceTest()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            try { MSBuildLocator.RegisterDefaults(); }
            catch { }
        }
    }

    public CleanRebuildEquivalenceTest()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"lurp-eq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _solutionPath = Path.Combine(_testDir, "Test.slnx");
        _dbPath = Path.Combine(_testDir, "index.db");
        _cleanRebuildDbPath = Path.Combine(_testDir, "clean.db");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
        }
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateTestSolution();

        await RunFullIndexAsync("A: full initial");
        ModifyOneFile();
        var snapshotB = await RunIncrementalIndexAsync("B: incremental after edit");
        var snapshotC = await RunIndependentFullIndexAsync("C: full after edit");

        SnapshotAssertions.CompareSnapshotsAreEquivalent(
            _dbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
    }

    private void CreateTestSolution()
    {
        var projDir = Path.Combine(_testDir, "src", "TestProject");
        Directory.CreateDirectory(projDir);

        File.WriteAllText(Path.Combine(projDir, "TestProject.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(
            Path.Combine(projDir, "Calculator.cs"),
            """
            namespace TestProject;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }
            }
            """);

        File.WriteAllText(
            Path.Combine(projDir, "Service.cs"),
            """
            namespace TestProject;

            public class Service
            {
                private readonly Calculator _calculator;

                public Service()
                {
                    _calculator = new Calculator();
                }

                public int Compute(int x, int y)
                {
                    return _calculator.Add(x, y);
                }
            }
            """);

        File.WriteAllText(_solutionPath, """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/TestProject/TestProject.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void ModifyOneFile()
    {
        var calculatorPath = Path.Combine(_testDir, "src", "TestProject", "Calculator.cs");
        File.WriteAllText(calculatorPath,
            """
            namespace TestProject;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Subtract(int a, int b)
                {
                    return a - b;
                }

                public int Multiply(int a, int b)
                {
                    return a * b;
                }
            }
            """);
    }

    private async Task<string> RunFullIndexAsync(string label)
    {
        Console.WriteLine($"--- {label} ---");

        if (File.Exists(_dbPath))
            File.Delete(_dbPath);

        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(_solutionPath);
        var gitRoot = _testDir;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        var snapshotId = SnapshotIdentity.Create(workspaceInfo, new HashSet<string>());
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
        var snapshotIdStr = snapshotId.ToString();

        manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath: null);

        var allEdges = new List<EdgeRecord>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
                continue;

            var projectName = project.Name;
            Console.WriteLine($"    [{projectName}]");

            var result = CompilationFactExtractor.ExtractAll(
                compilation, workspaceInfo, snapshotIdStr, projectName,
                new CompilationFactExtractor.ExtractionOptions(new HashSet<string>()));

            store.SaveDeclarations(snapshotIdStr, result.Declarations);
            store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
            store.SaveBindingIncompleteness(snapshotIdStr, result.BindingIncompleteness);
            if (result.Annotations.Count > 0)
                store.SaveAnnotations(snapshotIdStr, result.Annotations);

            allEdges.AddRange(result.Edges);

            Console.WriteLine($"      {result.Declarations.Count} symbols, {result.Edges.Count} edges");
        }

        var dedupedEdges = EdgeDedup.Deduplicate(allEdges);
        store.SaveEdges(snapshotIdStr, dedupedEdges);

        store.DeleteOrphanEdges(snapshotIdStr);
        store.BuildSearchIndex(snapshotIdStr);
        store.MarkSnapshotComplete(snapshotIdStr);

        Console.WriteLine($"    Snapshot: {snapshotIdStr}");
        return snapshotIdStr;
    }

    private async Task<string> RunIndependentFullIndexAsync(string label)
    {
        Console.WriteLine($"--- {label} ---");

        if (File.Exists(_cleanRebuildDbPath))
            File.Delete(_cleanRebuildDbPath);

        using var store = new SqliteIndexStore(_cleanRebuildDbPath);
        store.Open();
        store.RunMigrations();

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(_solutionPath);
        var gitRoot = _testDir;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        var snapshotId = SnapshotIdentity.Create(workspaceInfo, new HashSet<string>());
        var manifest = SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
        var snapshotIdStr = snapshotId.ToString();

        manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath: null);

        var allEdges = new List<EdgeRecord>();

        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation == null)
                continue;

            var projectName = project.Name;
            Console.WriteLine($"    [{projectName}]");

            var result = CompilationFactExtractor.ExtractAll(
                compilation, workspaceInfo, snapshotIdStr, projectName,
                new CompilationFactExtractor.ExtractionOptions(new HashSet<string>()));

            store.SaveDeclarations(snapshotIdStr, result.Declarations);
            store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
            store.SaveBindingIncompleteness(snapshotIdStr, result.BindingIncompleteness);
            if (result.Annotations.Count > 0)
                store.SaveAnnotations(snapshotIdStr, result.Annotations);

            allEdges.AddRange(result.Edges);

            Console.WriteLine($"      {result.Declarations.Count} symbols, {result.Edges.Count} edges");
        }

        var dedupedEdges = EdgeDedup.Deduplicate(allEdges);
        store.SaveEdges(snapshotIdStr, dedupedEdges);

        store.DeleteOrphanEdges(snapshotIdStr);
        store.BuildSearchIndex(snapshotIdStr);
        store.MarkSnapshotComplete(snapshotIdStr);

        Console.WriteLine($"    Snapshot: {snapshotIdStr}");
        return snapshotIdStr;
    }

    private async Task<string> RunIncrementalIndexAsync(string label)
    {
        Console.WriteLine($"--- {label} ---");

        using var store = new SqliteIndexStore(_dbPath);
        store.Open();

        using var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(_solutionPath);
        var gitRoot = _testDir;
        var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

        var previousManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);
        if (previousManifest == null)
            throw new InvalidOperationException("No previous snapshot found. Cannot run incremental index.");

        var incrementalIndexer = new IncrementalIndexer(
            store, gitRoot, [],
            jsonExportPath: null);

        var result = await incrementalIndexer.RunIncrementalAsync(
            solution, workspaceInfo, previousManifest);

        store.PruneOldSnapshots(keep: 3);

        Console.WriteLine($"    New snapshot: {result.NewSnapshotId}");
        return result.NewSnapshotId;
    }
}
