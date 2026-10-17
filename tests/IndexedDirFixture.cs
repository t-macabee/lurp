using Lurp.Storage;
using Lurp.Workspace;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

/// <summary>
///     A temp directory holding a migrated <c>index.db</c> with the workspace row the
///     CLI handlers join against, and zero or more complete snapshots (<c>s1</c>,
///     GUID ids in workspace <c>w1</c>, oldest first). With <c>stalePin</c> the oldest snapshot is
///     pinned and then marked failed, which is the stale-pin shape the pin, annotation
///     and timings tests describe.
/// </summary>
internal static class IndexedDirFixture
{
    internal static string Create(int completeSnapshots, bool stalePin = false)
    {
        return Create(completeSnapshots, stalePin, out _, out _);
    }

    internal static string Create(int completeSnapshots, bool stalePin, out string staleId, out string latestId)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-indexed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "index.db");
        // Snapshot ids are GUIDs (SnapshotId.Parse rejects anything else), so the
        // tests that reach the snapshot manifest work against the same shape as
        // production.
        var ids = Enumerable.Range(1, completeSnapshots).Select(_ => Guid.NewGuid().ToString()).ToArray();

        using (var store = new SqliteIndexStore(dbPath))
        {
            store.Open();
            store.RunMigrations();
            store.SaveWorkspace("w1", "gitroot", "solution.sln");
            for (var i = 0; i < ids.Length; i++)
            {
                store.SaveSnapshot(new SnapshotRow
                {
                    SnapshotId = ids[i],
                    WorkspaceId = "w1",
                    GitRoot = "gitroot",
                    SolutionPath = "solution.sln",
                    CreatedAtUtc = DateTime.UtcNow.AddMinutes(i),
                    DatabaseSchemaVersion = VersionConstants.DatabaseSchemaVersion,
                    OutputSchemaVersion = VersionConstants.OutputSchemaVersion,
                    ExtractorVersion = VersionConstants.ExtractorVersion,
                    ToolVersion = VersionConstants.ToolVersion
                });
                store.MarkSnapshotComplete(ids[i]);
            }

            if (stalePin && ids.Length > 0)
                store.SetPinnedSnapshot(ids[0]);
        }

        staleId = ids.Length > 0 ? ids[0] : string.Empty;
        latestId = ids.Length > 0 ? ids[^1] : string.Empty;

        if (stalePin)
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE snapshots SET status = 'failed' WHERE snapshot_id = @stale;";
            command.Parameters.AddWithValue("@stale", staleId);
            command.ExecuteNonQuery();
        }

        return dir;
    }
}
