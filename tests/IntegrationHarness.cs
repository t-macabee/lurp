using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Build.Locator;

namespace Lurp.Tests;

/// <summary>
///     Minimal self-host integration harness. Exists so the self-host acceptance
///     test can index the real <c>Lurp.slnx</c> and assemble a capsule over a live
///     symbol without going through <see cref="IntegrationTestBase" />, whose
///     <c>SolutionPath</c>/<c>TestDir</c> are bound to a generated fixture solution.
/// </summary>
internal static class IntegrationHarness
{
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

    public static async Task<string> RunFullIndexAsync(string dbPath, string solutionPath, string outputDir)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        store.ValidateSchema(VersionConstants.DatabaseSchemaVersion);
        try
        {
            await IndexRunner.RunAsync(
                store, solutionPath, outputDir,
                [], null, "full",
                false, null, false, false, CancellationToken.None);

            return store.LoadLatestSnapshot()?.SnapshotId
                   ?? throw new InvalidOperationException($"No snapshot found in {dbPath} after full index.");
        }
        finally
        {
            store.Close();
        }
    }

    public static SqliteIndexStore OpenReadStore(string dbPath)
    {
        return HandlerBootstrap.OpenStore(dbPath);
    }
}