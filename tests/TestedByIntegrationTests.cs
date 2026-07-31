using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;
using EdgeKind = Lurp.Storage.EdgeKind;

namespace Lurp.Storage.Tests;

/// <summary>
/// Integration tests proving TestAdapter emits TestedBy edges at type-level granularity
/// when indexing a real fixture solution.
/// </summary>
public sealed class TestedByIntegrationTests : IDisposable
{
    private string? _runDirectory;

    public void Dispose()
    {
        if (_runDirectory != null && Directory.Exists(_runDirectory))
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(_runDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [SkippableFact]
    public async Task FullIndex_OutcomeBenchmark_EmitsTestedByOnProductionType()
    {
        Skip.IfNot(IntegrationHarness.TryRegisterMSBuild(),
            "MSBuild is not available on this system.");

        var fixtureRoot = LocateFixtureRoot();
        _runDirectory = Path.Combine(
            Path.GetTempPath(), $"lurp_testedby_integration_{Guid.NewGuid():N}");
        CopyDirectory(fixtureRoot, _runDirectory);

        var solutionPath = Path.Combine(_runDirectory, "OutcomeBenchmark.slnx");
        var dbPath = Path.Combine(_runDirectory, "index.db");

        // Run a full index
        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);
        store.RunMigrations();
        string snapshotId;
        try
        {
            await IndexRunner.RunAsync(store, solutionPath, _runDirectory,
                skipAdapters: [], jsonExportPath: null, strategyArg: "full");
            snapshotId = store.GetLatestSnapshotId()
                ?? throw new InvalidOperationException("Full index completed but no snapshot id was returned.");
        }
        finally
        {
            store.Close();
        }

        // Query all TestedBy edges
        List<EdgeRecord> edges;
        using (var readStore = IntegrationHarness.OpenReadStore(dbPath))
        {
            edges = readStore.GetEdgesByKind(snapshotId, EdgeKind.TestedBy.ToString());
        }

        // Must be non-empty: TestAdapter must have found test-to-production references
        Assert.NotEmpty(edges);

        // Source IDs must be type-level (doc-comment prefix "T:"), not method-level ("M:")
        Assert.All(edges, e => Assert.StartsWith("T:", e.SourceSymbolId));
    }

    private static string LocateFixtureRoot()
        => Path.Combine(LocateRepositoryRoot(), "tests", "fixtures", "OutcomeBenchmark");

    private static string LocateRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "Lurp.slnx")))
            current = current.Parent;
        return current?.FullName
            ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.GetDirectories(sourceDir))
            CopyDirectory(directory, Path.Combine(destDir, Path.GetFileName(directory)));
    }
}
