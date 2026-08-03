using System;
using System.IO;
using System.Threading.Tasks;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// End-to-end regression coverage for compiler-language-version fidelity
/// (context-capsule completeness audit task #1).
///
/// MSBuildWorkspace can silently fall back to C# 7.3 parse options when a
/// project fails to evaluate. <c>tests/fixtures/LanguageVersionFallback/</c>
/// reproduces the fidelity question with an SDK-style project that sets no
/// <c>&lt;LangVersion&gt;</c> and modern C# source (file-scoped namespaces):
/// whatever effective language version the workspace derives must compile the
/// modern syntax (zero CS8370) and bind the controller-to-interface calls as
/// persisted edges. The second project pins <c>&lt;LangVersion&gt;9.0</c> and
/// must be honored (C# 9 record source extracts, no CS8370). The workspace
/// loader's application of <see cref="Workspace.LanguageVersionRecovery"/>
/// before extraction sees the solution is covered deterministically in
/// <see cref="WorkspaceLoaderTests"/>.
///
/// The fixture projects carry a real <c>&lt;TargetFramework&gt;</c> so their
/// compilations resolve a reference set and pass the unreadable-workspace gate
/// (a reference-less compilation is refused before extraction; a project that
/// cannot load a corlib has no observable language-version behavior either).
/// </summary>
public sealed class LanguageVersionRegressionTests : IDisposable
{
    private string? _testDir;

    public void Dispose()
    {
        if (_testDir != null && Directory.Exists(_testDir))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_testDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [SkippableFact]
    public async Task LanguageVersionFallback_ModernSyntax_NoCs8370_And_ControllerCallsBind()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        _testDir = Path.Combine(Path.GetTempPath(), $"lurp_langver_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var dbPath = Path.Combine(_testDir, "index.db");

        // Deliberately do NOT run dotnet build or restore: the fixture's
        // language-version behavior is exercised through MSBuildWorkspace's own
        // project load, and the framework reference set resolves from the SDK's
        // targeting pack without a restore.
        var solutionPath = IntegrationHarness.CopyNamedFixtureToTemp(_testDir, "LanguageVersionFallback");
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, _testDir);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        // Completion criterion part 1: the snapshot must not be complete while
        // carrying the mass C#-language-version failure pattern : the pattern
        // must be absent and the snapshot complete.
        var status = ScalarString(conn, "SELECT status FROM snapshots WHERE snapshot_id = @id", snapshotId);
        Assert.Equal("complete", status);

        var cs8370Count = ScalarLong(conn,
            "SELECT COUNT(*) FROM diagnostics WHERE snapshot_id = @id AND id = 'CS8370'", snapshotId);
        Assert.Equal(0L, cs8370Count);

        // Completion criterion part 2: modern syntax produces bound
        // controller-to-interface Calls edges, not only diagnostics/incompleteness.
        var getMyCoursesCalls = ScalarLong(conn, @"
            SELECT COUNT(*) FROM edges
            WHERE snapshot_id = @id AND kind = 'Calls'
              AND source_symbol_id LIKE '%InstructorCourseController.GetMyCourses%'
              AND target_symbol_id LIKE '%ICourseService.GetPagedForInstructorAsync%'", snapshotId);
        Assert.True(getMyCoursesCalls > 0,
            $"Expected a Calls edge InstructorCourseController.GetMyCourses -> ICourseService.GetPagedForInstructorAsync, got {getMyCoursesCalls}.");

        var getByIdCalls = ScalarLong(conn, @"
            SELECT COUNT(*) FROM edges
            WHERE snapshot_id = @id AND kind = 'Calls'
              AND source_symbol_id LIKE '%InstructorCourseController.GetById%'
              AND target_symbol_id LIKE '%ICourseService.GetByIdForInstructorAsync%'", snapshotId);
        Assert.True(getByIdCalls > 0,
            $"Expected a Calls edge InstructorCourseController.GetById -> ICourseService.GetByIdForInstructorAsync, got {getByIdCalls}.");

        // The second fixture project pins LangVersion=9.0 explicitly. Even though
        // its MSBuild evaluation also fails, the explicit version must be honored
        // (C# 9 record source extracts; no CS8370) : not clobbered by recovery.
        var explicitLangCs8370 = ScalarLong(conn,
            "SELECT COUNT(*) FROM diagnostics WHERE snapshot_id = @id AND project_name = 'ExplicitLang' AND id = 'CS8370'", snapshotId);
        Assert.Equal(0L, explicitLangCs8370);

        var recordSymbols = ScalarLong(conn,
            "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @id AND symbol_id LIKE '%ExplicitLang.WeatherForecast%'", snapshotId);
        Assert.True(recordSymbols > 0,
            $"Expected the C#9 record ExplicitLang.WeatherForecast to be extracted, got {recordSymbols}.");
    }

    [SkippableFact]
    public async Task LanguageVersionFallback_CapsulePreservesInterfaceDispatchProvenance()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system. Cannot run integration test.");

        _testDir = Path.Combine(Path.GetTempPath(), $"lurp_dispatch_provenance_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        var dbPath = Path.Combine(_testDir, "index.db");
        var solutionPath = IntegrationHarness.CopyNamedFixtureToTemp(_testDir, "LanguageVersionFallback");
        var snapshotId = await IntegrationHarness.RunFullIndexAsync(dbPath, solutionPath, _testDir);

        using var store = IntegrationHarness.OpenReadStore(dbPath);
        var courseService = store.ResolveSymbolByFqn("Modern.CourseService", snapshotId)
            ?? throw new InvalidOperationException("Modern.CourseService was not indexed.");
        var courseCapsule = ContextAssembler.ResolveAndAssemble(
            store, store,
            new ContextLookup(snapshotId, courseService.SymbolId.Value, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, Budget: 100_000, MaxHops: 3),
            store, store);

        var mediatedController = Assert.Single(courseCapsule.DirectCallers,
            item => item.SymbolId.Contains("InstructorCourseController.GetById", StringComparison.Ordinal));
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, mediatedController.Relationship);
        Assert.False(mediatedController.Direct);
        Assert.Equal("possible", mediatedController.Provenance);
        Assert.Contains("Calls", mediatedController.InclusionReason);
        Assert.Contains("MayDispatchTo", mediatedController.InclusionReason);

        var directCaller = Assert.Single(courseCapsule.DirectCallers,
            item => item.SymbolId.Contains("DirectCourseServiceCaller.GetByIdDirectly", StringComparison.Ordinal));
        Assert.Equal(CapsuleRelationship.DirectCaller, directCaller.Relationship);
        Assert.True(directCaller.Direct);
        Assert.Equal("compiler_proved", directCaller.Provenance);

        var controller = store.ResolveSymbolByFqn("Modern.Api.InstructorCourseController", snapshotId)
            ?? throw new InvalidOperationException("Modern.Api.InstructorCourseController was not indexed.");
        var controllerCapsule = ContextAssembler.ResolveAndAssemble(
            store, store,
            new ContextLookup(snapshotId, controller.SymbolId.Value, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, Budget: 100_000, MaxHops: 3),
            store, store);
        var projectedCallee = Assert.Single(controllerCapsule.DirectCallees,
            item => item.SymbolId.Contains("Modern.CourseService.GetByIdForInstructorAsync", StringComparison.Ordinal));
        Assert.Equal("global_implementation_relation", projectedCallee.Provenance);
        Assert.Equal(CapsuleRelationship.IndirectDispatchCandidate, projectedCallee.Relationship);
        Assert.False(projectedCallee.Direct);
        Assert.Contains("Calls", projectedCallee.InclusionReason);
        Assert.Contains("MayDispatchTo", projectedCallee.InclusionReason);

        var directCallerType = store.ResolveSymbolByFqn("Modern.DirectCourseServiceCaller", snapshotId)
            ?? throw new InvalidOperationException("Modern.DirectCourseServiceCaller was not indexed.");
        var directCallerCapsule = ContextAssembler.ResolveAndAssemble(
            store, store,
            new ContextLookup(snapshotId, directCallerType.SymbolId.Value, null, null),
            new ContextAssemblyOptions(ContextIntent.Inspect, Budget: 100_000, MaxHops: 3),
            store, store);
        var directCallee = Assert.Single(directCallerCapsule.DirectCallees,
            item => item.SymbolId.Contains("Modern.CourseService.GetByIdForInstructorAsync", StringComparison.Ordinal));
        Assert.Equal(CapsuleRelationship.DirectCallee, directCallee.Relationship);
        Assert.True(directCallee.Direct);
        Assert.Equal("compiler_proved", directCallee.Provenance);

        var interfaceMember = store.ResolveSymbolByFqn("Modern.ICourseService.GetByIdForInstructorAsync", snapshotId)
            ?? throw new InvalidOperationException("Modern.ICourseService.GetByIdForInstructorAsync was not indexed.");
        var implementationCandidates = store.GetOutgoingEdges(snapshotId, interfaceMember.SymbolId.Value)
            .Where(edge => edge.Kind == "MayDispatchTo")
            .ToList();
        Assert.True(implementationCandidates.Count >= 2,
            $"Expected at least two source-level ICourseService implementations, got {implementationCandidates.Count}.");
        Assert.All(implementationCandidates, edge => Assert.Equal("compiler_proved", edge.Provenance));
    }

    private static long ScalarLong(SqliteConnection conn, string sql, string snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
    }

    private static string ScalarString(SqliteConnection conn, string sql, string snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", snapshotId);
        return Convert.ToString(cmd.ExecuteScalar() ?? "") ?? "";
    }
}
