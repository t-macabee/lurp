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
    private static readonly object MSBuildRegistrationLock = new();

    /// <summary>
    ///     Registers the MSBuild that ships with the .NET SDK. The test project itself
    ///     cannot be built without that SDK, so registration cannot fail on a supported
    ///     platform: any exception is a real failure and must not be turned into a skip.
    /// </summary>
    public static void EnsureMSBuildRegistered()
    {
        lock (MSBuildRegistrationLock)
        {
            if (MSBuildLocator.IsRegistered)
                return;

            MSBuildLocator.RegisterDefaults();
        }
    }

    public static async Task<string> RunFullIndexAsync(string dbPath, string solutionPath)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var store = new SqliteIndexStore(dbPath);
        store.Open();
        store.RunMigrations();
        try
        {
            await IndexRunner.RunAsync(
                store, solutionPath,
                [], "full",
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