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
    private readonly string _cleanRebuildDbPath;
    private readonly string _solutionPath;

    public PipelineEquivalenceTest()
    {
        _testDir = Path.Combine(
            Path.GetTempPath(),
            $"lurp_equiv_test_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testDir);
        _dbPath = Path.Combine(_testDir, "index.db");
        _cleanRebuildDbPath = Path.Combine(_testDir, "index-clean.db");
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

        var snapshotC = await RunIndependentFullIndexAsync("Index C (full after change)");

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

        var snapshotC = await RunIndependentFullIndexAsync("Index C (full after change)");

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

        var snapshotC = await RunIndependentFullIndexAsync("Index C (full rebuild after deletion)");

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

    // Closure-validation fixture: removing a partial part. `is_partial` is a
    // whole-symbol property (DeclaringSyntaxReferences.Length > 1), so a
    // copied-forward declaration row for the surviving part would keep
    // is_partial=1 where a full rebuild yields 0.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterPartialPartRemoval()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "partial part removal",
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

                public partial class Widget
                {
                    public void Second() { }
                }
                """)),
            () => File.Delete(
                Path.Combine(_testDir, "src", "TestProject", "Part2.cs")));
    }

    // Closure-validation fixture: the only link from the unchanged consumer to
    // the changed type runs through an implicit default constructor, whose edge
    // carries a null source document path and is dropped from the BFS.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterImplicitConstructorTargetEdit()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "implicit constructor target edit",
            () => CreateSingleProjectSolution(
                ("Model.cs", """
                namespace TestProject;

                public class Model
                {
                }
                """),
                ("Consumer.cs", """
                namespace TestProject;

                public class Consumer
                {
                    public Model Create() => new Model();
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Model.cs"),
                """
                namespace TestProject;

                public class Model
                {
                    public int Value { get; set; }
                }
                """));
    }

    // Closure-validation fixture: the edit adds an interface member that an
    // existing method in an unchanged file now implicitly implements. No
    // previous edge runs from the implementing document to the new member, so
    // the reverse-edge BFS has no arc to follow.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterImplicitImplementationAppears()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "implicit implementation appears",
            () => CreateSingleProjectSolution(
                ("IRunner.cs", """
                namespace TestProject;

                public interface IRunner
                {
                }
                """),
                ("Runner.cs", """
                namespace TestProject;

                public class Runner : IRunner
                {
                    public void Run() { }
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "IRunner.cs"),
                """
                namespace TestProject;

                public interface IRunner
                {
                    void Run();
                }
                """));
    }

    // Closure-validation fixture: the unchanged caller holds a reference that
    // does not bind before the edit. An unresolved binding produced no edge, so
    // the closure — which reads only persisted edges — cannot reach it.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterUnresolvedReferenceBinds()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "unresolved reference binds",
            () => CreateSingleProjectSolution(
                ("Helper.cs", """
                namespace TestProject;

                public static class Helper
                {
                }
                """),
                ("Caller.cs", """
                namespace TestProject;

                public class Caller
                {
                    public int Invoke() => Helper.Compute();
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Helper.cs"),
                """
                namespace TestProject;

                public static class Helper
                {
                    public static int Compute() => 42;
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

    // Adapter-scoping fixture (task B6). The DI registration lives in
    // CompositionRoot.cs, which is never edited; the edit replaces the registered
    // implementation type in Services.cs. Once adapters honor ScopeDocuments, the
    // registration site is re-walked only if the closure reaches it — which it does
    // here through the previous snapshot's DI edge, whose source_document_path is
    // CompositionRoot.cs. This is the case that distinguishes a correctly scoped
    // adapter from one that silently drops the edge; the sibling
    // AfterDependencyInjectionRegistrationEdit fixture puts the registration and the
    // edit in the same file, so it cannot.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterRegisteredTypeEditedInOtherDocument()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "registered type edited in another document",
            () => CreateSingleProjectSolution(("CompositionRoot.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }

                    public static class ServiceCollectionServiceExtensions
                    {
                        public static IServiceCollection AddScoped<TService, TImplementation>(
                            this IServiceCollection services)
                            where TImplementation : TService => services;
                    }
                }

                namespace TestProject
                {
                    using Microsoft.Extensions.DependencyInjection;

                    public class CompositionRoot
                    {
                        public void Configure(IServiceCollection services)
                        {
                            services.AddScoped<IService, Service>();
                        }
                    }
                }
                """), ("Services.cs", """
                namespace TestProject;

                public interface IService { }

                public class Service : IService
                {
                    public int Value => 1;
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Services.cs"),
                """
                namespace TestProject;

                public interface IService
                {
                    int Value { get; }
                }

                public class Service : IService
                {
                    public int Value => 2;
                    public string Extra => "added";
                }
                """));
    }

    // Convention-scan closure fixture (task B6). The Scan site lives in
    // CompositionRoot.cs, which is never edited; the edit adds a new class in
    // Services.cs that the convention now matches. The previous snapshot holds no
    // registration edge targeting the new type (it did not exist), and no symbol
    // in the edited document is the target of any previous edge, so the
    // reverse-edge BFS has no arc from Services.cs to the Scan site and the
    // registration document is never pulled into the extraction scope. Explicit
    // generic registrations do not share this shape: their previous edge's
    // source_document_path is the registration document, so the closure reaches
    // it (the sibling AfterRegisteredTypeEditedInOtherDocument fixture).
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterConventionMatchedTypeAddedInOtherDocument()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "convention-matched type added in another document",
            () => CreateSingleProjectSolution(("CompositionRoot.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                }

                namespace Scrutor
                {
                    public interface ITypeSourceSelector { }
                    public interface IImplementationTypeSelector { }
                    public interface IServiceTypeSelector { }
                    public interface IImplementationTypeFilter
                    {
                        IImplementationTypeFilter AssignableTo<T>();
                    }

                    public static class ServiceCollectionExtensions
                    {
                        public static Microsoft.Extensions.DependencyInjection.IServiceCollection Scan(
                            this Microsoft.Extensions.DependencyInjection.IServiceCollection services,
                            System.Action<ITypeSourceSelector> action) => services;
                    }

                    public static class TypeSourceSelectorExtensions
                    {
                        public static IImplementationTypeSelector FromAssembliesOf<T>(this ITypeSourceSelector source) => null!;
                        public static IImplementationTypeSelector AddClasses(this IImplementationTypeSelector source) => null!;
                        public static IImplementationTypeSelector AddClasses(this IImplementationTypeSelector source, System.Action<IImplementationTypeFilter> filter) => null!;
                        public static IServiceTypeSelector AsImplementedInterfaces(this IImplementationTypeSelector source) => null!;
                    }

                    public static class ServiceTypeSelectorExtensions
                    {
                        public static Microsoft.Extensions.DependencyInjection.IServiceCollection WithScopedLifetime(this IServiceTypeSelector source) => null!;
                    }
                }

                namespace TestProject
                {
                    using Microsoft.Extensions.DependencyInjection;
                    using Scrutor;

                    public class CompositionRoot
                    {
                        public void Configure(IServiceCollection services)
                        {
                            services.Scan(scan => scan
                                .FromAssembliesOf<IService>()
                                .AddClasses()
                                .AsImplementedInterfaces()
                                .WithScopedLifetime());
                        }
                    }
                }
                """), ("Services.cs", """
                namespace TestProject;

                public interface IService { }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Services.cs"),
                """
                namespace TestProject;

                public interface IService { }

                public class Service : IService { }
                """));
    }

    // Adapter-scoping fixture (prompt 2, EF Core). The DbContext and its
    // OnModelCreating walk live in AppDbContext.cs, the edited entity type in
    // Widget.cs. The reverse-edge closure pulls AppDbContext.cs into the
    // extraction scope through the previous snapshot's MapsTo edge (its
    // source_document_path is AppDbContext.cs), so the scoped EF Core walk
    // re-emits the annotation and the path-scoped annotation delete must
    // retire the copied-forward row first. Before the delete path existed the
    // unscoped walk duplicated every copied-forward annotation in the
    // incremental snapshot, so this fixture fails on the old behavior and
    // passes once extraction and deletion narrow in lockstep.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_AfterEntityTypeEditedInOtherDocument()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        await AssertIncrementalMatchesFullRebuildAsync(
            "EF entity type edited in another document",
            () => CreateSingleProjectSolution(("AppDbContext.cs", """
                namespace Microsoft.EntityFrameworkCore
                {
                    public class DbContext
                    {
                        protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
                    }
                    public class DbSet<TEntity> where TEntity : class { }
                    public class ModelBuilder
                    {
                        public EntityTypeBuilder<TEntity> Entity<TEntity>() where TEntity : class => null!;
                    }
                    public class EntityTypeBuilder<TEntity> where TEntity : class
                    {
                        public EntityTypeBuilder<TEntity> HasDatabaseName(string name) => this;
                    }
                }

                namespace TestProject
                {
                    public class AppDbContext : Microsoft.EntityFrameworkCore.DbContext
                    {
                        public Microsoft.EntityFrameworkCore.DbSet<Widget> Widgets { get; set; } = null!;

                        protected override void OnModelCreating(Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder)
                        {
                            modelBuilder.Entity<Widget>().HasDatabaseName("widgets");
                        }
                    }
                }
                """), ("Widget.cs", """
                namespace TestProject;

                public class Widget
                {
                    public string Name { get; set; } = "";
                }
                """)),
            () => File.WriteAllText(
                Path.Combine(_testDir, "src", "TestProject", "Widget.cs"),
                """
                namespace TestProject;

                public class Widget
                {
                    public string Name { get; set; } = "";
                    public int Count { get; set; }
                }
                """));
    }

    private async Task<string> RunFullIndexAsync(string label, bool deleteFirst = true, string? dbPath = null)
    {
        Console.WriteLine($"--- {label} ---");

        dbPath ??= _dbPath;

        if (deleteFirst && File.Exists(dbPath))
            File.Delete(dbPath);

        using var store = new SqliteIndexStore(dbPath);
        store.Open();
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

            var snapshotId = SnapshotIdentity.Create(workspaceInfo, new HashSet<string>());
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
                    new CompilationFactExtractor.ExtractionOptions(new HashSet<string>()));

                store.SaveDeclarations(snapshotIdStr, result.Declarations);
                totalDeclarations += result.Declarations.Count;

                allEdges.AddRange(result.Edges);

                store.SaveDiagnostics(snapshotIdStr, result.Diagnostics);
                totalDiagnostics += result.Diagnostics.Count;
                store.SaveBindingIncompleteness(snapshotIdStr, result.BindingIncompleteness);
                if (result.Annotations.Count > 0)
                    store.SaveAnnotations(snapshotIdStr, result.Annotations);

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
        }
    }

    private Task<string> RunIndependentFullIndexAsync(string label)
        => RunFullIndexAsync(label, deleteFirst: true, dbPath: _cleanRebuildDbPath);

    private async Task<string> RunIncrementalIndexAsync(string label)
    {
        Console.WriteLine($"--- {label} ---");

        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

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
            store, gitRoot, [],
            jsonExportPath: null);

        string? fallbackLabel = null;
        try
        {
            var result = await incrementalIndexer.RunIncrementalAsync(
                solution, workspaceInfo, previousManifest);

            Console.WriteLine($"    New snapshot: {result.NewSnapshotId}");
            return result.NewSnapshotId;
        }
        catch (FullRebuildRequiredException ex)
        {
            Console.WriteLine($"    Full rebuild required: {ex.Message}");
            fallbackLabel = label + " (fallback)";
        }
        finally
        {
            store.PruneOldSnapshots(keep: 3);
        }

        if (fallbackLabel != null)
            return await RunFullIndexAsync(fallbackLabel, deleteFirst: false);

        throw new InvalidOperationException("Unreachable");
    }

    private void CompareSnapshotsAreEquivalent(string snapshotB, string snapshotC)
    {
        SnapshotAssertions.CompareSnapshotsAreEquivalent(
            _dbPath, snapshotB, _cleanRebuildDbPath, snapshotC);
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
        var snapshotC = await RunIndependentFullIndexAsync(
            $"Index C (full after {scenario})");

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

    [SkippableFact]
    public async Task IncrementalIndex_ThreeProjectReverseClosure_MatchesFullIncludingDiagnostics()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateThreeProjectDependencyClosureSolution();
        await RunFullIndexAsync("Index A (full initial, three-project closure)");

        File.WriteAllText(Path.Combine(_testDir, "src", "ProjectA", "Contract.cs"), """
            namespace ProjectA;

            public interface IContract
            {
                int Get();
            }
            """);

        var incremental = await RunIncrementalIndexAsync("Index B (incremental, A contract changed)");
        var full = await RunIndependentFullIndexAsync("Index C (full after A contract changed)");

        CompareSnapshotsAreEquivalent(incremental, full);
    }

    private void CreateThreeProjectDependencyClosureSolution()
    {
        var projectADir = Path.Combine(_testDir, "src", "ProjectA");
        var projectBDir = Path.Combine(_testDir, "src", "ProjectB");
        var projectCDir = Path.Combine(_testDir, "src", "ProjectC");
        Directory.CreateDirectory(projectADir);
        Directory.CreateDirectory(projectBDir);
        Directory.CreateDirectory(projectCDir);

        File.WriteAllText(Path.Combine(projectADir, "ProjectA.csproj"), ProjectFile());
        File.WriteAllText(Path.Combine(projectADir, "Contract.cs"), """
            namespace ProjectA;
            public interface IContract { string Get(); }
            """);

        File.WriteAllText(Path.Combine(projectBDir, "ProjectB.csproj"), ProjectFile("ProjectA"));
        File.WriteAllText(Path.Combine(projectBDir, "Implementation.cs"), """
            namespace ProjectB;
            public sealed class Implementation : ProjectA.IContract
            {
                public string Get() => "value";
            }
            """);

        File.WriteAllText(Path.Combine(projectCDir, "ProjectC.csproj"), ProjectFile("ProjectB"));
        File.WriteAllText(Path.Combine(projectCDir, "Caller.cs"), """
            namespace ProjectC;
            public sealed class Caller
            {
                public string Run(ProjectB.Implementation implementation) => implementation.Get();
            }
            """);

        File.WriteAllText(_solutionPath, """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/ProjectA/ProjectA.csproj" />
                <Project Path="src/ProjectB/ProjectB.csproj" />
                <Project Path="src/ProjectC/ProjectC.csproj" />
              </Folder>
            </Solution>
            """);

        static string ProjectFile(string? reference = null)
            => reference == null
                ? """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>"""
                : $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup><ItemGroup><ProjectReference Include="..\{reference}\{reference}.csproj" /></ItemGroup></Project>""";
    }

    // T16a: Regression test for CrossDocumentEdgeRefresher : verifies that
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

        var snapshotC = await RunIndependentFullIndexAsync("Index C (full after Library change)");

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // T16b: Regression test for the absolute/relative path mismatch in
    // binding-incompleteness invalidation. The cross-document refresh passed
    // absolute document paths (C:/.../Calculator.cs) to
    // DeleteBindingIncompletenessByDocumentPaths, which compares document_path
    // ordinally against the relative paths written by
    // BindingIncompletenessCollector : the delete was a silent no-op and stale
    // incompleteness rows survived the refresh. The assertion must travel
    // through the refresh path itself: a store-level unit test against the
    // delete API cannot observe the defect (it passes either way), and the
    // full incremental pipeline masks it because PrepareSnapshotData already
    // deletes rows for the whole invalidation scope before the refresh runs.
    [SkippableFact]
    public async Task CrossDocumentRefresh_DeletesStaleBindingIncompletenessByRelativePath()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateCrossProjectDependentTestSolution();

        // Full index produces the previous snapshot with the real incoming edge
        // from App/Calculator.cs to Library.Widget.GetLabel.
        var previousSnapshotId = await RunFullIndexAsync("Index A (full initial, cross-project)");

        var refreshSnapshotId = $"snap-refresh-{Guid.NewGuid():N}";

        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();

            // The refresh writes into a real snapshot, so the target snapshot
            // must exist as a well-formed header row before any per-snapshot
            // table is populated : foreign keys are enforced.
            var previous = store.LoadLatestSnapshot()
                ?? throw new InvalidOperationException("The full index did not persist a snapshot.");
            store.SaveSnapshot(new SnapshotRow
            {
                SnapshotId = refreshSnapshotId,
                WorkspaceId = previous.WorkspaceId,
                GitRoot = previous.GitRoot,
                SolutionPath = previous.SolutionPath,
                SdkVersion = previous.SdkVersion,
                CompilerVersion = previous.CompilerVersion,
                CreatedAtUtc = DateTime.UtcNow,
                Documents = previous.Documents,
                DatabaseSchemaVersion = previous.DatabaseSchemaVersion,
                OutputSchemaVersion = previous.OutputSchemaVersion,
                ExtractorVersion = previous.ExtractorVersion,
                ToolVersion = previous.ToolVersion,
                PreviousSnapshotId = previousSnapshotId,
                Projects = previous.Projects,
                SkippedAdapters = previous.SkippedAdapters,
            });

            // Simulate the copy-forward step: the stale row : an ambiguity that
            // no longer occurs : exists in the new snapshot before the refresh.
            store.SaveBindingIncompleteness(refreshSnapshotId,
            [
                new BindingIncompletenessRecord(
                    "App",
                    "src/App/Calculator.cs",
                    BindingIncompletenessReason.AmbiguousOverload,
                    Count: 1,
                    VersionConstants.ExtractorVersion),
            ]);
            store.Close();
        }

        using (var workspace = MSBuildWorkspace.Create())
        {
            workspace.RegisterWorkspaceFailedHandler(args =>
                Console.Error.WriteLine($"  [Workspace] {args.Diagnostic.Kind}: {args.Diagnostic.Message}"));
            var solution = await workspace.OpenSolutionAsync(_solutionPath);
            var workspaceInfo = new WorkspaceInfo(solution, _testDir);

            using var store = new SqliteIndexStore(_dbPath);
            store.Open();
            var refresher = new CrossDocumentEdgeRefresher(store, _testDir, []);
            var changedPaths = new HashSet<string> { "src/Library/Widget.cs" };

            var processed = await refresher.RefreshAsync(
                solution, workspaceInfo, refreshSnapshotId, previousSnapshotId,
                changedPaths, alreadyExtractedPaths: null, CancellationToken.None);

            Assert.True(processed > 0,
                "The refresh must re-extract the App document that references the changed Library symbol.");

            var records = store.GetBindingIncompleteness(refreshSnapshotId);
            Assert.DoesNotContain(records, record =>
                record.ProjectName == "App"
                && record.DocumentPath == "src/App/Calculator.cs"
                && record.Reason == BindingIncompletenessReason.AmbiguousOverload);
        }
    }

    // T15: Regression test for DocumentVersionId : verifies that two files with
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

        using (var store = new SqliteIndexStore(_dbPath))
        {
            store.Open();

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

        var snapshotB = await RunIndependentFullIndexAsync("Index B (second full, duplicate content)");
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
        // ProjectA: Library : defines Widget (the type that will change)
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

        // ProjectB: App : references Library and uses Widget
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

    // Task 3 regression: same-project two-document convergence : when a
    // declaration in one file changes and an unchanged caller in the same
    // project references it, the cross-document edge refresher must
    // re-extract the unchanged caller's edges so the incremental snapshot
    // matches a clean rebuild.
    [SkippableFact]
    public async Task IncrementalIndex_Matches_FullRebuild_WhenSameProjectSignatureChanges()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        CreateSingleProjectSolution(
            ("Calculator.cs", """
                namespace TestProject;

                public class Calculator
                {
                    public int Add(int a, int b)
                    {
                        return a + b;
                    }
                }
                """),
            ("Service.cs", """
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
                """));

        var snapshotA = await RunFullIndexAsync("Index A (full initial, same-project)");

        // Change only Calculator.Add's signature : Service.cs is untouched
        // but its edges referencing Calculator.Add must be refreshed.
        File.WriteAllText(
            Path.Combine(_testDir, "src", "TestProject", "Calculator.cs"),
            """
            namespace TestProject;

            public class Calculator
            {
                public int Add(int a, int b, int c)
                {
                    return a + b + c;
                }
            }
            """);

        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental, same-project signature change)");

        var snapshotC = await RunIndependentFullIndexAsync("Index C (full after same-project change)");

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // Task 3 regression: configuration-only change (project reference added)
    // must not return the previous snapshot : it must fall back to a full
    // rebuild and produce a result equivalent to a clean rebuild.
    [SkippableFact]
    public async Task IncrementalIndex_ConfigOnlyProjectReferenceChange_FallsBackToEquivalentFullRebuild()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        // Create a solution with two independent projects (no reference)
        var projADir = Path.Combine(_testDir, "src", "ProjectA");
        Directory.CreateDirectory(projADir);
        File.WriteAllText(Path.Combine(projADir, "ProjectA.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>");
        File.WriteAllText(Path.Combine(projADir, "Foo.cs"), """
            namespace ProjectA;

            public class Foo
            {
                public string Bar() => "bar";
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
        File.WriteAllText(Path.Combine(projBDir, "Baz.cs"), """
            namespace ProjectB;

            public class Baz
            {
                public int Value() => 42;
            }
            """);

        File.WriteAllText(_solutionPath, """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/ProjectA/ProjectA.csproj" />
                <Project Path="src/ProjectB/ProjectB.csproj" />
              </Folder>
            </Solution>
            """);

        var snapshotA = await RunFullIndexAsync("Index A (full initial, no project ref)");

        // Now add a project reference from ProjectB to ProjectA : no source
        // files change, only the project graph configuration.
        File.WriteAllText(Path.Combine(projBDir, "ProjectB.csproj"), @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include=""..\ProjectA\ProjectA.csproj"" />
  </ItemGroup>
</Project>");

        // Incremental must detect the project-reference change and fall back
        // to a full rebuild rather than returning the previous snapshot.
        var snapshotB = await RunIncrementalIndexAsync("Index B (incremental, config-only change : should fallback)");

        Assert.NotEqual(snapshotA, snapshotB);

        var snapshotC = await RunIndependentFullIndexAsync("Index C (full after config change)");

        CompareSnapshotsAreEquivalent(snapshotB, snapshotC);
    }

    // T4 regression: SaveEdges must not throw SQLite Error 19 when given
    // duplicate (source, target, kind) triples : e.g. from cross-project
    // extraction or multiple extractors producing the same relation.
    [SkippableFact]
    public void SaveEdges_DeduplicatesSameTripleAcrossProjects()
    {
        Skip.If(!MSBuildLocator.IsRegistered,
            "MSBuild is not available on this system. Cannot run integration test.");

        using var store = new SqliteIndexStore(_dbPath);
        store.Open();
        store.RunMigrations();

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

    // T6 regression: when two edges with the same (source, target, kind) key
    // and equal provenance collide, the losing edge's TypeArgumentsJson must
    // be merged into the winner so no instantiation evidence is discarded.
    [Fact]
    public void EdgeDedup_EqualProvenance_MergesTypeArgumentsJson()
    {
        var edgeA = new EdgeRecord
        {
            SourceSymbolId = "S", TargetSymbolId = "T", Kind = "MayDispatchTo",
            Provenance = "compiler_proved", ExtractorVersion = "v1",
            TypeArgumentsJson = "[\"Customer\"]",
        };
        var edgeB = new EdgeRecord
        {
            SourceSymbolId = "S", TargetSymbolId = "T", Kind = "MayDispatchTo",
            Provenance = "compiler_proved", ExtractorVersion = "v1",
            TypeArgumentsJson = "[\"Order\"]",
        };

        var result = EdgeDedup.Deduplicate(new[] { edgeA, edgeB });

        Assert.Single(result);
        Assert.Contains("Customer", result[0].TypeArgumentsJson);
        Assert.Contains("Order", result[0].TypeArgumentsJson);
    }

    [Fact]
    public void EdgeDedup_LowerProvenanceLosesButTypeArgumentsJsonSurvives()
    {
        var high = new EdgeRecord
        {
            SourceSymbolId = "S", TargetSymbolId = "T", Kind = "MayDispatchTo",
            Provenance = "compiler_proved", ExtractorVersion = "v1",
            TypeArgumentsJson = "[\"VariantA\"]",
        };
        var low = new EdgeRecord
        {
            SourceSymbolId = "S", TargetSymbolId = "T", Kind = "MayDispatchTo",
            Provenance = "runtime_unknown", ExtractorVersion = "v1",
            TypeArgumentsJson = "[\"VariantB\"]",
        };

        var result = EdgeDedup.Deduplicate(new[] { low, high });

        Assert.Single(result);
        Assert.Equal("compiler_proved", result[0].Provenance);
        Assert.Contains("VariantA", result[0].TypeArgumentsJson);
        Assert.Contains("VariantB", result[0].TypeArgumentsJson);
    }

    [Fact]
    public void EdgeDedup_MergeTypeArguments_DetectsSingleVariantFormat()
    {
        var result = EdgeDedup.MergeTypeArguments("[\"A\",\"B\"]", "[\"A\",\"C\"]");

        Assert.NotNull(result);
        Assert.Contains("[\"A\",\"B\"]", result);
        Assert.Contains("[\"A\",\"C\"]", result);
    }

    [Fact]
    public void EdgeDedup_MergeTypeArguments_MergesMultiVariantFormats()
    {
        var result = EdgeDedup.MergeTypeArguments("[[\"A\"],[\"B\"]]", "[[\"B\"],[\"C\"]]");

        Assert.NotNull(result);
        Assert.Contains("[\"A\"]", result);
        Assert.Contains("[\"B\"]", result);
        Assert.Contains("[\"C\"]", result);
    }

    [Fact]
    public void EdgeDedup_MergeTypeArguments_NullInputsHandled()
    {
        var result = EdgeDedup.MergeTypeArguments(null, "[\"A\"]");

        Assert.NotNull(result);
        Assert.Contains("[\"A\"]", result);
    }

    [Fact]
    public void EdgeDedup_Deduplicate_SameTypeArgumentsJson_StillCanonicalizesFormat()
    {
        var edgeA = new EdgeRecord
        {
            SourceSymbolId = "S", TargetSymbolId = "T", Kind = "MayDispatchTo",
            Provenance = "possible", ExtractorVersion = "v1",
            TypeArgumentsJson = "[\"Same\"]",
        };
        var edgeB = new EdgeRecord
        {
            SourceSymbolId = "S", TargetSymbolId = "T", Kind = "MayDispatchTo",
            Provenance = "possible", ExtractorVersion = "v1",
            TypeArgumentsJson = "[\"Same\"]",
        };

        var result = EdgeDedup.Deduplicate(new[] { edgeA, edgeB });

        Assert.Single(result);
        Assert.Contains("Same", result[0].TypeArgumentsJson);
    }
}
