using System;
using System.IO;
using System.Threading.Tasks;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Storage.Tests;

/// <summary>
/// Harness that drives the real <see cref="IndexRunner.RunAsync"/> entrypoint
/// against a committed fixture solution. This is the point of T19 : previous
/// integration tests hand-rolled the pipeline (CompilationFactExtractor + save
/// calls) and never exercised the CLI's actual code path.
/// </summary>
public static class IntegrationHarness
{
    /// <summary>
    /// Resolve the committed fixture root relative to the test assembly location.
    /// </summary>
    public static string GetFixtureRoot()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(IntegrationHarness).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot determine test assembly location.");

        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "fixtures", "Sample"));
    }

    /// <summary>
    /// Copy the committed fixture to a temp directory and return the path to the .slnx.
    /// </summary>
    public static string CopyFixtureToTemp(string testDir)
    {
        var fixtureRoot = GetFixtureRoot();
        CopyDirectory(fixtureRoot, testDir);
        return Path.Combine(testDir, "Sample.slnx");
    }

    /// <summary>
    /// Copy a named committed fixture (<c>tests/fixtures/&lt;name&gt;/</c>) to a
    /// temp directory and return the path to its <c>.slnx</c>.
    /// </summary>
    public static string CopyNamedFixtureToTemp(string testDir, string fixtureName)
    {
        var assemblyDir = Path.GetDirectoryName(typeof(IntegrationHarness).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot determine test assembly location.");
        var fixtureRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "fixtures", fixtureName));
        CopyDirectory(fixtureRoot, testDir);
        return Path.Combine(testDir, $"{fixtureName}.slnx");
    }

    /// <summary>
    /// Run a full index through <see cref="IndexRunner.RunAsync"/> and return the snapshot id.
    /// </summary>
    public static async Task<string> RunFullIndexAsync(string dbPath, string solutionPath, string outputDir)
    {
        var store = CreateAndOpenStore(dbPath);

        try
        {
            await IndexRunner.RunAsync(
                store,
                solutionPath,
                outputDir,
                skipAdapters: [],
                jsonExportPath: null,
                strategyArg: "full");

            return store.GetLatestSnapshotId()
                ?? throw new InvalidOperationException("Full index completed but no snapshot id was returned.");
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Run an incremental index through <see cref="IndexRunner.RunAsync"/> and return the snapshot id.
    /// </summary>
    public static async Task<string> RunIncrementalIndexAsync(string dbPath, string solutionPath, string outputDir)
    {
        var store = CreateAndOpenStore(dbPath);

        try
        {
            await IndexRunner.RunAsync(
                store,
                solutionPath,
                outputDir,
                skipAdapters: [],
                jsonExportPath: null,
                strategyArg: "incremental");

            return store.GetLatestSnapshotId()
                ?? throw new InvalidOperationException("Incremental index completed but no snapshot id was returned.");
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Open a store for read-only queries (raw SQL assertions, snapshot comparisons).
    /// </summary>
    public static SqliteIndexStore OpenReadStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    /// <summary>
    /// Returns true when MSBuild is available (registered or registrable).
    /// Callers should combine with <c>Skip.IfNot()</c> inside a
    /// <c>[SkippableFact]</c> so the test is skipped : not failed : when
    /// the environment lacks a .NET SDK.
    /// </summary>
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

    private static SqliteIndexStore CreateAndOpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        return store;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }
}
