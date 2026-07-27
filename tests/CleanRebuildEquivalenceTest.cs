using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lurp;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

public sealed class PipelineEquivalenceTest : IAsyncLifetime, IDisposable
{
    private readonly string _testDir;
    private readonly string _dbPath;
    private readonly string _solutionPath;

    public PipelineEquivalenceTest()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_equiv_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");
        _solutionPath = Path.Combine(_testDir, "TestSolution.slnx");
    }

    public async Task InitializeAsync()
    {

        if (!MSBuildLocator.IsRegistered)
        {
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch
            {

            }
        }

        CreateTestSolution();
    }

    public Task DisposeAsync()
    {

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnectionClearAllPools();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, recursive: true); }
            catch { }
        }
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterSingleFileChange()
    {

        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        var snapshotA = await RunFullIndexAsync("Index A (full initial)");

        ModifyOneFile();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental)");

        var snapshotC = await RunFullIndexAsync("Index C (full after change)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // Regression test for a scoping gap in DeleteEdgesWithNullDocumentPathForAssemblies:
    // edges sourced from symbols with no DeclaringSyntaxReferences (e.g. an
    // implicit default constructor) carry a NULL source_document_path, which
    // can't be scoped to a project by path. If the incremental indexer
    // deletes all null-path edges snapshot-wide instead of only those
    // belonging to re-extracted projects, an untouched project's null-path
    // edges are deleted and never regenerated, causing the incremental
    // snapshot to silently lose edges relative to a full rebuild.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_WhenUnaffectedProjectHasImplicitMembers()
    {

        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateMultiProjectTestSolution();

        var snapshotA = await RunFullIndexAsync("Index A (full initial, multi-project)");

        ModifyFileInProjectB();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental, only ProjectB touched)");

        var snapshotC = await RunFullIndexAsync("Index C (full after change)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // Regression test for PruneRemovedSymbols: when a file is deleted and no
    // replacement document versions exist (pure-deletion path), the early
    // return skipped all pruning, leaving stale symbols from the deleted file
    // in the copied-forward snapshot.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterFileDeletion()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateFileDeletionTestSolution();

        var snapshotA = await RunFullIndexAsync("Index A (full initial)");

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM edges
                WHERE snapshot_id = @snapshotId
                  AND source_document_path = 'src/TestProject/Models.cs';";
            command.Parameters.AddWithValue("@snapshotId", snapshotA);

            Assert.True(Convert.ToInt32(command.ExecuteScalar()) > 0,
                "Precondition: the deleted source file must have an outgoing edge.");
        }

        DeleteModelsFile();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental after deletion)");

        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var command = conn.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM edges e
                WHERE e.snapshot_id = @snapshotId
                  AND (
                      NOT EXISTS (
                          SELECT 1 FROM snapshot_symbols ss
                          WHERE ss.snapshot_id = e.snapshot_id
                            AND ss.symbol_id = e.source_symbol_id
                      )
                      OR NOT EXISTS (
                          SELECT 1 FROM snapshot_symbols ss
                          WHERE ss.snapshot_id = e.snapshot_id
                            AND ss.symbol_id = e.target_symbol_id
                      )
                  );";
            command.Parameters.AddWithValue("@snapshotId", snapshotB);

            Assert.Equal(0, Convert.ToInt32(command.ExecuteScalar()));
        }

        var snapshotC = await RunFullIndexAsync("Index C (full rebuild after deletion)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterSignatureEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "signature edit",
            () => CreateSingleProjectSolution(("Calculator.cs", """
                namespace TestProject;

                public class Calculator
                {
                    public int Compute(int value) => value + 1;
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Calculator.cs"),
                """
                namespace TestProject;

                public class Calculator
                {
                    public long Compute(long value) => value + 1;
                }
                """));
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterOperatorAndConversionCallEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "operator and conversion call edit",
            () => CreateSingleProjectSolution(("Money.cs", """
                namespace TestProject;

                public sealed class Money
                {
                    public static Money operator +(Money left, Money right) => left;
                    public static explicit operator int(Money value) => 1;

                    public int Convert() => 1;
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Money.cs"),
                """
                namespace TestProject;

                public sealed class Money
                {
                    public static Money operator +(Money left, Money right) => left;
                    public static explicit operator int(Money value) => 1;

                    public int Convert()
                    {
                        var total = this + this;
                        return (int)total;
                    }
                }
                """));
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterBodyOnlyEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "body-only edit",
            () => CreateSingleProjectSolution(("Calculator.cs", """
                namespace TestProject;

                public class Calculator
                {
                    public int Compute(int value) => value + 1;
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Calculator.cs"),
                """
                namespace TestProject;

                public class Calculator
                {
                    public int Compute(int value) => value + 2;
                }
                """));
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterDocumentMove()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "document move",
            () => CreateSingleProjectSolution(("Original.cs", """
                namespace TestProject;

                public class MovedType
                {
                    public string GetValue() => "original";
                }
                """)),
            () =>
            {
                var sourcePath = Path.Combine(_testDir, "src", "TestProject", "Original.cs");
                var movedDirectory = Path.Combine(_testDir, "src", "TestProject", "Moved");
                Directory.CreateDirectory(movedDirectory);
                File.Move(sourcePath, Path.Combine(movedDirectory, "Original.cs"));
            });
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterPartialClassEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "partial class edit",
            () => CreateSingleProjectSolution(
                ("Part1.cs", """
                namespace TestProject;

                public partial class Widget
                {
                    public void First() { }
                }
                """),
                ("Part2.cs", """
                namespace TestProject;

                public partial class Widget { }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Part2.cs"),
                """
                namespace TestProject;

                public partial class Widget
                {
                    public void Second() { }
                }
                """));
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterBaseAndInterfaceEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "base and interface edit",
            () => CreateSingleProjectSolution(("Types.cs", """
                namespace TestProject;

                public class BaseA { }
                public class BaseB { }
                public interface IMarker { }

                public class Widget : BaseA { }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Types.cs"),
                """
                namespace TestProject;

                public class BaseA { }
                public class BaseB { }
                public interface IMarker { }

                public class Widget : BaseB, IMarker { }
                """));
    }

    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterDependencyInjectionRegistrationEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "dependency injection registration edit",
            () => CreateSingleProjectSolution(("CompositionRoot.cs", """
                namespace Microsoft.Extensions.DependencyInjection;

                public interface IServiceCollection { }

                public static class ServiceCollectionServiceExtensions
                {
                    public static IServiceCollection AddScoped<TService, TImplementation>(
                        this IServiceCollection services)
                        where TImplementation : TService => services;
                }
                """), ("Services.cs", """
                using Microsoft.Extensions.DependencyInjection;

                namespace TestProject;

                public interface IService { }
                public class Service : IService { }
                public class OtherService : IService { }

                public class CompositionRoot
                {
                    public void Configure(IServiceCollection services)
                    {
                        services.AddScoped<IService, Service>();
                    }
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Services.cs"),
                """
                using Microsoft.Extensions.DependencyInjection;

                namespace TestProject;

                public interface IService { }
                public class Service : IService { }
                public class OtherService : IService { }

                public class CompositionRoot
                {
                    public void Configure(IServiceCollection services)
                    {
                        services.AddScoped<IService, OtherService>();
                    }
                }
                """));
    }

    private async Task<string> RunFullIndexAsync(string label, bool deleteFirst = true)
    {
        Console.WriteLine($"--- {label} ---");

        if (deleteFirst && File.Exists(_dbPath))
            File.Delete(_dbPath);

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {

                Console.Error.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}");
            });

            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var gitRoot = _testDir;
            var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

            var snapshotId = SnapshotId.New();
            var manifest = global::Lurp.Workspace.SnapshotManifest.FromWorkspace(workspaceInfo, snapshotId);
            var snapshotIdStr = snapshotId.ToString();

            manifest.Save(store, workspaceInfo.DocumentContents, jsonExportPath: null);

            int totalDeclarations = 0;
            int totalEdges = 0;
            int totalDiagnostics = 0;
            var allEdges = new List<EdgeRecord>();

            foreach (var (project, compilation) in await GetAllAsync(solution))
            {
                var projectName = project.Name;
                Console.WriteLine($"    [{projectName}]");

                var result = CompilationFactExtractor.ExtractAll(
                    compilation, workspaceInfo, snapshotIdStr, projectName,
                    skipAdapters: new HashSet<string>());

                store.SaveDeclarations(snapshotIdStr, result.Declarations);
                totalDeclarations += result.Declarations.Count;

                allEdges.AddRange(result.Edges);

                store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                totalDiagnostics += result.Diagnostics.Count;

                Console.WriteLine($"      {result.Declarations.Count} symbols, {result.Edges.Count} edges, {result.Diagnostics.Count} diagnostics.");
            }

            var dedupedEdges = EdgeDedup.Deduplicate(allEdges);
            store.SaveEdges(snapshotIdStr, dedupedEdges);
            totalEdges = dedupedEdges.Count;

            var previousManifest = store.LoadLatestSnapshot(manifest.WorkspaceId.Value);
            if (previousManifest != null && previousManifest.SnapshotId != snapshotIdStr)
            {
                var differ = new Workspace.SemanticDiffer(store, store, store);
                var (diffChanges, _) = differ.ComputeDiff(previousManifest.SnapshotId, snapshotIdStr);
                store.SaveSemanticChanges(previousManifest.SnapshotId, snapshotIdStr, diffChanges);
            }

            store.DeleteOrphanEdges(snapshotIdStr);
            store.BuildSearchIndex(snapshotIdStr);
            store.MarkSnapshotComplete(snapshotIdStr);

            Console.WriteLine($"    Snapshot: {snapshotIdStr}");
            return snapshotIdStr;
        }
        finally
        {
            store.PruneOldSnapshots(keep: 3);
            store.Close();
        }
    }

    private async Task<string> RunIncrementalIndexAsync(string label)
    {
        Console.WriteLine($"--- {label} ---");

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            using var workspace = MSBuildWorkspace.Create();
            workspace.RegisterWorkspaceFailedHandler(args =>
            {
                Console.Error.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}");
            });

            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var gitRoot = _testDir;
            var workspaceInfo = new WorkspaceInfo(solution, gitRoot);

            var previousManifest = store.LoadLatestSnapshot(workspaceInfo.Id.Value);

            if (previousManifest == null)
                throw new InvalidOperationException("No previous snapshot found. Cannot run incremental index.");

            var incrementalIndexer = new IncrementalIndexer(
                store, gitRoot, _solutionPath, _testDir,
                skipAdapters: [],
                jsonExportPath: null);

            var result = await incrementalIndexer.RunIncrementalAsync(
                solution, workspaceInfo, previousManifest);

            Console.WriteLine($"    New snapshot: {result.NewSnapshotId}");
            return result.NewSnapshotId;
        }
        finally
        {
            store.PruneOldSnapshots(keep: 3);
            store.Close();
        }
    }

    private void CompareSnapshotsAreEquivalent(string snapshotB, string snapshotC)
    {
        SnapshotAssertions.CompareSnapshotsAreEquivalent(_dbPath, snapshotB, snapshotC);
    }

    private async Task AssertIncrementalMatchesFullRebuildAsync(
        string scenario,
        Action createSolution,
        Action modifySolution)
    {
        createSolution();

        var snapshotA = await RunFullIndexAsync($"Index A (full initial, {scenario})");
        modifySolution();
        var snapshotB = await RunIncrementalIndexAsync($"Index B (incremental, {scenario})");
        var snapshotC = await RunFullIndexAsync(
            $"Index C (full after {scenario})",
            deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    private void CreateTestSolution()
    {

        var projDir = Path.Combine(_testDir, "src", "TestProject");
        Directory.CreateDirectory(projDir);

        var csprojPath = Path.Combine(projDir, "TestProject.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
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

        File.WriteAllText(
            Path.Combine(projDir, "Models.cs"),
            """
            namespace TestProject;

            public class User
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }

            public class Product
            {
                public string Id { get; set; } = "";
                public decimal Price { get; set; }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/TestProject/TestProject.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void CreateSingleProjectSolution(params (string Path, string Content)[] files)
    {
        var projectDirectory = Path.Combine(_testDir, "src", "TestProject");
        if (Directory.Exists(projectDirectory))
            Directory.Delete(projectDirectory, recursive: true);
        Directory.CreateDirectory(projectDirectory);

        File.WriteAllText(Path.Combine(projectDirectory, "TestProject.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);

        foreach (var (relativePath, content) in files)
        {
            var filePath = Path.Combine(projectDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, content);
        }

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

                // Added method
                public int Multiply(int a, int b)
                {
                    return a * b;
                }
            }
            """);
    }

    private void CreateMultiProjectTestSolution()
    {
        var projADir = Path.Combine(_testDir, "src", "ProjectA");
        Directory.CreateDirectory(projADir);

        File.WriteAllText(Path.Combine(projADir, "ProjectA.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // No explicit constructor: Roslyn synthesizes an implicit parameterless
        // constructor with no DeclaringSyntaxReferences, so the edge sourced from
        // it has a NULL source_document_path. ProjectA is never touched by the
        // incremental run in the test below, so its edges must survive unchanged.
        File.WriteAllText(Path.Combine(projADir, "Widgets.cs"), """
            namespace ProjectA;

            public class Widget
            {
                public string Name { get; set; } = "";
            }

            public class Gadget
            {
                public int Count { get; set; }
            }
            """);

        var projBDir = Path.Combine(_testDir, "src", "ProjectB");
        Directory.CreateDirectory(projBDir);

        File.WriteAllText(Path.Combine(projBDir, "ProjectB.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(Path.Combine(projBDir, "Calculator.cs"), """
            namespace ProjectB;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/ProjectA/ProjectA.csproj" />
                <Project Path="src/ProjectB/ProjectB.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void ModifyFileInProjectB()
    {
        var calculatorPath = Path.Combine(_testDir, "src", "ProjectB", "Calculator.cs");
        File.WriteAllText(calculatorPath,
            """
            namespace ProjectB;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                // Added method
                public int Multiply(int a, int b)
                {
                    return a * b;
                }
            }
            """);
    }

    private void CreateFileDeletionTestSolution()
    {
        var projDir = Path.Combine(_testDir, "src", "TestProject");
        Directory.CreateDirectory(projDir);

        var csprojPath = Path.Combine(projDir, "TestProject.csproj");
        File.WriteAllText(csprojPath, @"<Project Sdk=""Microsoft.NET.Sdk"">
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
            Path.Combine(projDir, "Models.cs"),
            """
            namespace TestProject;

            public class User
            {
                public string Name { get; set; } = "";
                public int Age { get; set; }
            }

            public class Product
            {
                public string Id { get; set; } = "";
                public decimal Price { get; set; }
            }

            public class ModelFactory
            {
                public Calculator CreateCalculator() => new Calculator();
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/TestProject/TestProject.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void DeleteModelsFile()
    {
        var modelsPath = Path.Combine(_testDir, "src", "TestProject", "Models.cs");
        File.Delete(modelsPath);
    }

    // T16a: Regression test for CrossDocumentEdgeRefresher — verifies that
    // when a symbol in ProjectA changes, documents in ProjectB that reference
    // the changed symbol through cross-project edges have their edges
    // correctly re-extracted during incremental indexing. Without the
    // CrossDocumentEdgeRefresher, edges sourced from ProjectB documents
    // that point at changed ProjectA symbols would be stale (copied forward
    // from the previous snapshot with no re-extraction), and the incremental
    // snapshot would silently diverge from a full rebuild.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_WhenDependentProjectReferencesChangedSymbol()
    {

        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateCrossProjectDependentTestSolution();

        var snapshotA = await RunFullIndexAsync("Index A (full initial, cross-project)");

        ModifyLibraryFile();

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental, Library changed)");

        var snapshotC = await RunFullIndexAsync("Index C (full after Library change)", deleteFirst: false);

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // T15: Regression test for DocumentVersionId — verifies that two files with
    // identical byte content stored at different paths receive distinct
    // version IDs that include the document path, so consumers can tell them
    // apart and a second full rebuild produces an equivalent snapshot.
    [SkippableFact]
    public async Task TwoIdenticalFiles_GetDistinctVersionIds_AndRerunProducesEquivalence()
    {

        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateDuplicateContentTestSolution();

        var snapshotA = await RunFullIndexAsync("Index A (full, duplicate content)");

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        try
        {
            var versionIdsByPath = store.GetDocumentVersionIdsByPath(snapshotA);

            var path1 = "src/ProjectX/Widget.cs";
            var path2 = "src/ProjectY/Widget.cs";

            Assert.True(versionIdsByPath.ContainsKey(path1),
                $"Expected version ID for {path1}");
            Assert.True(versionIdsByPath.ContainsKey(path2),
                $"Expected version ID for {path2}");
            Assert.NotEqual(versionIdsByPath[path1], versionIdsByPath[path2]);

            var source1 = store.GetSource(path1, snapshotA);
            var source2 = store.GetSource(path2, snapshotA);
            Assert.NotNull(source1);
            Assert.NotNull(source2);
            Assert.Equal(source1, source2);
            Assert.Contains("class Widget", source1);
        }
        finally
        {
            store.Close();
        }

        var snapshotB = await RunFullIndexAsync("Index B (second full, duplicate content)", deleteFirst: false);
        CompareSnapshotsAreEquivalent(snapshotA, snapshotB);
    }

    private void CreateDuplicateContentTestSolution()
    {
        var projXDir = Path.Combine(_testDir, "src", "ProjectX");
        Directory.CreateDirectory(projXDir);

        File.WriteAllText(Path.Combine(projXDir, "ProjectX.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(Path.Combine(projXDir, "Widget.cs"), """
            namespace Widgets;

            public class Widget
            {
                public string Name { get; set; } = "";

                public string GetLabel()
                {
                    return Name;
                }
            }
            """);

        var projYDir = Path.Combine(_testDir, "src", "ProjectY");
        Directory.CreateDirectory(projYDir);

        File.WriteAllText(Path.Combine(projYDir, "ProjectY.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        // Write the same byte content to a different path.
        File.WriteAllText(Path.Combine(projYDir, "Widget.cs"), """
            namespace Widgets;

            public class Widget
            {
                public string Name { get; set; } = "";

                public string GetLabel()
                {
                    return Name;
                }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/ProjectX/ProjectX.csproj" />
                <Project Path="src/ProjectY/ProjectY.csproj" />
              </Folder>
            </Solution>
            """);
    }


    private void CreateCrossProjectDependentTestSolution()
    {
        // ProjectA: Library — defines Widget (the type that will change)
        var libDir = Path.Combine(_testDir, "src", "Library");
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(libDir, "Library.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");

        File.WriteAllText(Path.Combine(libDir, "Widget.cs"), """
            namespace Library;

            public class Widget
            {
                public string Name { get; set; } = "";

                public string GetLabel()
                {
                    return Name;
                }
            }
            """);

        // ProjectB: App — references Library and uses Widget
        var appDir = Path.Combine(_testDir, "src", "App");
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(appDir, "App.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\Library\Library.csproj"" />
  </ItemGroup>
</Project>");

        File.WriteAllText(Path.Combine(appDir, "Calculator.cs"), """
            namespace App;

            public class Calculator
            {
                private readonly Library.Widget _widget;

                public Calculator(Library.Widget widget)
                {
                    _widget = widget;
                }

                public string GetWidgetName()
                {
                    return _widget.GetLabel();
                }
            }
            """);

        File.WriteAllText(_solutionPath, $"""
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Library/Library.csproj" />
                <Project Path="src/App/App.csproj" />
              </Folder>
            </Solution>
            """);
    }

    private void ModifyLibraryFile()
    {
        var widgetPath = Path.Combine(_testDir, "src", "Library", "Widget.cs");
        File.WriteAllText(widgetPath, """
            namespace Library;

            public class WidgetBase
            {
                public string Name { get; set; } = "";
                public string GetLabel() => Name;
            }

            public class Widget : WidgetBase
            {
            }
            """);
    }

    private static async Task<List<(Project Project, Compilation Compilation)>> GetAllAsync(Solution solution)
    {
        var results = new List<(Project Project, Compilation Compilation)>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync();
            if (compilation != null)
                results.Add((project, compilation));
        }
        return results;
    }

    private static void SqliteConnectionClearAllPools()
    {
        SnapshotAssertions.SqliteConnectionClearAllPools();
    }

    private (int SourceRows, int SymbolRows) GetFtsCounts(string snapshotId)
    {
        return SnapshotAssertions.GetFtsCounts(_dbPath, snapshotId);
    }

    // T4 regression: SaveEdges must not throw SQLite Error 19 when given
    // duplicate (source, target, kind) triples — e.g. from cross-project
    // extraction or multiple extractors producing the same relation.
    [SkippableFact]
    public void SaveEdges_DeduplicatesSameTripleAcrossProjects()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        var store = new SqliteIndexStore(_dbPath);
        store.Open(_dbPath);
        store.RunMigrations();

        try
        {
            var snapshotId = "snap-t4-dedup";
            var edge1 = new EdgeRecord
            {
                SourceSymbolId = "T:Lib.IFoo|Lib",
                TargetSymbolId = "T:Lib.Bar|Lib",
                Kind = "Implements",
                Provenance = "compiler_proved",
                SnapshotId = snapshotId,
                ExtractorVersion = "v1",
                SourceDocumentPath = "src/Lib/Foo.cs",
            };
            var edge2 = new EdgeRecord
            {
                SourceSymbolId = "T:Lib.IFoo|Lib",
                TargetSymbolId = "T:Lib.Bar|Lib",
                Kind = "Implements",
                Provenance = "framework_derived",
                SnapshotId = snapshotId,
                ExtractorVersion = "v1",
                SourceDocumentPath = "src/App/Foo.cs",
            };

            store.SaveEdges(snapshotId, new[] { edge1, edge2 });

            var stored = store.GetEdges(snapshotId);
            Assert.Single(stored);
        }
        finally
        {
            store.Close();
        }
    }

    // T4 regression: EdgeDedup.Deduplicate must keep the highest-provenance
    // edge when the same (source, target, kind) triple is produced with
    // different provenance values.
    [Fact]
    public void EdgeDedup_KeepsHighestProvenance()
    {
        var low = new EdgeRecord
        {
            SourceSymbolId = "A", TargetSymbolId = "B", Kind = "Calls",
            Provenance = "runtime_unknown", ExtractorVersion = "v1",
        };
        var high = new EdgeRecord
        {
            SourceSymbolId = "A", TargetSymbolId = "B", Kind = "Calls",
            Provenance = "compiler_proved", ExtractorVersion = "v1",
        };

        var result = EdgeDedup.Deduplicate(new[] { low, high });

        Assert.Single(result);
        Assert.Equal("compiler_proved", result[0].Provenance);
    }
}
