using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     Audit Phase 7.8.2 (B6/Q12a): a full index of the C#-only CrossProject
///     fixture records the per-symbol TFM set. OnlyNet9/OnlyNet10 appear in one
///     TFM each, SharedTarget in both, and the single-TFM App project in one row.
/// </summary>
public sealed class SymbolTargetFrameworkAttributionTests
{
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] Net9Only = ["net9.0"];
    private static readonly string[] Net10Only = ["net10.0"];
    private static readonly string[] Net10AndNet9 = ["net10.0", "net9.0"];

    [Fact]
    public void FullIndex_MultiTargetFixture_AttributesTargetFrameworksPerSymbol()
    {
        var testDir = Path.Combine(Path.GetTempPath(), $"lurp-tfm-{Guid.NewGuid():N}");
        var treeRoot = Path.Combine(testDir, "GroundTruth");
        string? cacheDir = null;

        try
        {
            GroundTruthFixture.CopyTree(GroundTruthFixture.LocateFixtureRoot(), treeRoot);
            var solutionPath = Path.Combine(treeRoot, "CrossProject", "CrossProject.CSharp.slnx");
            Assert.True(File.Exists(solutionPath), $"CrossProject solution missing: {solutionPath}");

            GroundTruthFixture.RestoreSolution(solutionPath);
            cacheDir = LurpCache.ResolveSolutionCacheDir(solutionPath);

            var index = TargetTreeIntegrityTests.RunCli(IndexTimeout, "--mode=index", $"--solution={solutionPath}", "--strategy=full");
            Assert.True(index.ExitCode == 0, $"full index exited {index.ExitCode}:\n{index.Stdout}\n{index.Stderr}");

            var dbPath = Path.Combine(cacheDir, "index.db");
            Assert.True(File.Exists(dbPath), $"index.db not found at {dbPath}");

            using var store = new SqliteIndexStore(dbPath);
            store.Open();
            var snapshotId = store.LoadLatestSnapshot()?.SnapshotId
                             ?? throw new InvalidOperationException($"No snapshot in {dbPath} after full index.");

            Assert.Equal(Net9Only,
                SnapshotAssertions.ReadTargetFrameworksForSymbol(dbPath, snapshotId, "M:CrossProject.Lib.PublicApi.OnlyNet9"));
            Assert.Equal(Net10Only,
                SnapshotAssertions.ReadTargetFrameworksForSymbol(dbPath, snapshotId, "M:CrossProject.Lib.PublicApi.OnlyNet10"));
            Assert.Equal(Net10AndNet9,
                SnapshotAssertions.ReadTargetFrameworksForSymbol(dbPath, snapshotId, "M:CrossProject.Lib.PublicApi.SharedTarget"));
            Assert.Equal(Net10Only,
                SnapshotAssertions.ReadTargetFrameworksForSymbol(dbPath, snapshotId, "M:CrossProject.App.Consumer.RunPublic"));
        }
        finally
        {
            try { if (cacheDir != null && Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            try { if (Directory.Exists(testDir)) Directory.Delete(testDir, true); } catch { }
        }
    }
}
