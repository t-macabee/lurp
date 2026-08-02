using System;
using System.IO;
using System.Threading.Tasks;
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
        // carrying the mass C#-language-version failure pattern — the pattern
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
        // (C# 9 record source extracts; no CS8370) — not clobbered by recovery.
        var explicitLangCs8370 = ScalarLong(conn,
            "SELECT COUNT(*) FROM diagnostics WHERE snapshot_id = @id AND project_name = 'ExplicitLang' AND id = 'CS8370'", snapshotId);
        Assert.Equal(0L, explicitLangCs8370);

        var recordSymbols = ScalarLong(conn,
            "SELECT COUNT(*) FROM snapshot_symbols WHERE snapshot_id = @id AND symbol_id LIKE '%ExplicitLang.WeatherForecast%'", snapshotId);
        Assert.True(recordSymbols > 0,
            $"Expected the C#9 record ExplicitLang.WeatherForecast to be extracted, got {recordSymbols}.");
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
