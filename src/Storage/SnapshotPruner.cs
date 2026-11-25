using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotPruner(SqliteConnection connection)
{
    private static readonly string[] SnapshotChildTables =
    [
        "edges", "diagnostics", "snapshot_declarations", "snapshot_symbols", "symbol_target_frameworks",
        "projects", "snapshot_documents", "snapshot_build_inputs", "source_fts", "symbol_fts",
        "annotations", "snapshot_timings", "snapshot_metrics", "snapshot_graph_nodes", "binding_incompleteness"
    ];

    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal void PruneOldSnapshots(int keep = 3)
    {
        using var listCmd = _connection.CreateCommand();
        listCmd.CommandText = "SELECT DISTINCT workspace_id FROM snapshots;";
        var workspaceIds = new List<string>();
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
                workspaceIds.Add(reader.GetString(0));
        }

        foreach (var workspaceId in workspaceIds) PruneWorkspace(workspaceId, keep);
    }

    private void PruneWorkspace(string workspaceId, int keep)
    {
        var completeIds = ReadCompleteSnapshotIds(workspaceId);
        var failedToDelete = ReadPrunableFailedSnapshotIds(workspaceId);

        if (completeIds.Count <= keep && failedToDelete.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            var pinnedIds = GetPinnedSnapshotIds(workspaceId, transaction);
            var pruneIds = new List<string>(failedToDelete.Where(id => !pinnedIds.Contains(id)));
            if (completeIds.Count > keep)
                pruneIds.AddRange(completeIds.Where(id => !pinnedIds.Contains(id)).Skip(keep));

            if (pruneIds.Count == 0)
            {
                transaction.Commit();
                return;
            }

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;

            foreach (var sid in pruneIds) DeleteSnapshotData(cmd, sid);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private List<string> ReadCompleteSnapshotIds(string workspaceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot_id FROM snapshots
            WHERE workspace_id = @workspaceId
              AND status = @completeStatus
            ORDER BY selected_at_utc DESC;
            """;
        cmd.Parameters.AddWithValue("@workspaceId", workspaceId);
        cmd.Parameters.AddWithValue("@completeStatus", SnapshotStatusValues.Complete);

        var ids = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetString(0));
        return ids;
    }

    private List<string> ReadPrunableFailedSnapshotIds(string workspaceId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            SELECT f.snapshot_id,
                   CASE WHEN {SnapshotLifecycleStore.SupersededFailureExistsSql} THEN 1 ELSE 0 END AS is_superseded,
                   EXISTS (
                       SELECT 1 FROM annotations
                       WHERE snapshot_id = f.snapshot_id AND document_path IS NULL
                   ) AS has_user_annotations
            FROM snapshots f
            WHERE f.workspace_id = @workspaceId
              AND f.status = @failedStatus
            ORDER BY f.selected_at_utc DESC;
            """;
        cmd.Parameters.AddWithValue("@workspaceId", workspaceId);
        cmd.Parameters.AddWithValue("@failedStatus", SnapshotStatusValues.Failed);
        cmd.Parameters.AddWithValue("@completeStatus", SnapshotStatusValues.Complete);

        var toDelete = new List<string>();
        var keptCurrentFailure = false;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var isSuperseded = reader.GetInt32(1) == 1;
            if (isSuperseded || keptCurrentFailure)
            {
                if (reader.GetInt32(2) == 0)
                    toDelete.Add(id);
            }
            else
                keptCurrentFailure = true;
        }

        return toDelete;
    }

    internal void DeleteIncompleteSnapshots()
    {
        using var listCmd = _connection.CreateCommand();
        listCmd.CommandText = """
            SELECT snapshot_id
            FROM snapshots
            WHERE status IN (@inProgress, @failed)
              AND payload_pruned = 0;
            """;
        listCmd.Parameters.AddWithValue("@inProgress", SnapshotStatusValues.InProgress);
        listCmd.Parameters.AddWithValue("@failed", SnapshotStatusValues.Failed);
        var snapshotIds = new List<string>();
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
                snapshotIds.Add(reader.GetString(0));
        }

        if (snapshotIds.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;

            foreach (var snapshotId in snapshotIds)
            {
                DeleteSnapshotPayload(cmd, snapshotId, preserveUserAnnotations: true);
                // Keep failed rows for status and interrupted replacements for their user notes.
                cmd.CommandText = """
                    UPDATE snapshots SET payload_pruned = 1 WHERE snapshot_id = @sid;
                    DELETE FROM snapshots
                    WHERE snapshot_id = @sid AND status = @inProgress
                      AND NOT EXISTS (
                          SELECT 1 FROM annotations
                          WHERE snapshot_id = @sid AND document_path IS NULL
                      );
                    """;
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@sid", snapshotId);
                cmd.Parameters.AddWithValue("@inProgress", SnapshotStatusValues.InProgress);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal void DeleteSnapshotData(string snapshotId)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            if (IsPinnedSnapshot(snapshotId, transaction))
                throw new InvalidOperationException($"snapshot '{snapshotId}' is pinned and cannot be deleted. Clear the pin first with --mode=pin-snapshot --clear.");

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            DeleteSnapshotData(cmd, snapshotId);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal void DeleteSnapshotForRebuild(string snapshotId, SqliteTransaction transaction, bool preservePreviousPin = false)
    {
        if (IsPinnedSnapshot(snapshotId, transaction))
            throw new InvalidOperationException($"snapshot '{snapshotId}' is pinned and cannot be deleted. Clear the pin first with --mode=pin-snapshot --clear.");

        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        DeleteSnapshotData(cmd, snapshotId, preserveUserAnnotations: true, preservePreviousPin: preservePreviousPin);
    }

    internal void PromoteSnapshot(string stagingId, string targetId, SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        // The target id is restored before commit, so references to it remain valid.
        cmd.CommandText = "PRAGMA defer_foreign_keys = ON;";
        cmd.ExecuteNonQuery();

        DeleteSnapshotForRebuild(targetId, transaction, preservePreviousPin: true);

        cmd.Parameters.AddWithValue("@stagingId", stagingId);
        cmd.Parameters.AddWithValue("@targetId", targetId);
        cmd.CommandText = "UPDATE snapshots SET snapshot_id = @targetId WHERE snapshot_id = @stagingId;";
        cmd.ExecuteNonQuery();

        foreach (var table in SnapshotChildTables)
        {
            // Search indexes are built directly under the target id.
            if (table is "source_fts" or "symbol_fts")
                continue;
            cmd.CommandText = $"UPDATE {table} SET snapshot_id = @targetId WHERE snapshot_id = @stagingId;";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = "UPDATE semantic_changes SET to_snapshot_id = @targetId WHERE to_snapshot_id = @stagingId;";
        cmd.ExecuteNonQuery();
    }

    private static void DeleteSnapshotData(SqliteCommand cmd, string snapshotId, bool preserveUserAnnotations = false, bool preservePreviousPin = false)
    {
        DeleteSnapshotPayload(cmd, snapshotId, preserveUserAnnotations);

        if (!preservePreviousPin)
        {
            cmd.CommandText = "UPDATE snapshot_pins SET previous_pinned_snapshot_id = NULL WHERE previous_pinned_snapshot_id = @sid;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@sid", snapshotId);
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = "DELETE FROM snapshots WHERE snapshot_id = @sid;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteSnapshotPayload(SqliteCommand cmd, string snapshotId, bool preserveUserAnnotations)
    {
        // project_references point at projects by row id, so this snapshot's
        // reference rows must go before the projects rows themselves.
        cmd.CommandText = """
            DELETE FROM project_references
            WHERE project_id IN (
                SELECT pr.project_id
                FROM project_references pr
                JOIN projects p ON p.project_id = pr.project_id
                WHERE p.snapshot_id = @sid
            );
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.ExecuteNonQuery();

        foreach (var table in SnapshotChildTables)
        {
            // Table names come from the fixed list above. Only the id is bound.
            cmd.CommandText = table == "annotations" && preserveUserAnnotations
                ? "DELETE FROM annotations WHERE snapshot_id = @sid AND document_path IS NOT NULL;"
                : $"DELETE FROM {table} WHERE snapshot_id = @sid;";
            cmd.ExecuteNonQuery();
        }

        // Clean up orphaned project references
        cmd.CommandText = """
            DELETE FROM project_references
            WHERE project_id NOT IN (SELECT project_id FROM projects);
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        // Clean up orphaned declarations that no snapshot links to
        cmd.CommandText = """
            DELETE FROM declarations
            WHERE NOT EXISTS (
                SELECT 1 FROM snapshot_declarations sdecl
                WHERE sdecl.declaration_id = declarations.declaration_id
            );
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        // Clean up orphaned symbols no longer referenced by any declaration
        cmd.CommandText = """
            DELETE FROM symbols
            WHERE symbol_id NOT IN (SELECT DISTINCT symbol_id FROM declarations);
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "DELETE FROM semantic_changes WHERE from_snapshot_id = @sid OR to_snapshot_id = @sid;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.ExecuteNonQuery();

        // Foreign keys are enforced, but they declare no ON DELETE action, so document
        // data that no retained snapshot references is reclaimed explicitly.
        cmd.CommandText = """
            DELETE FROM document_versions
            WHERE document_version_id NOT IN (
                SELECT DISTINCT document_version_id FROM snapshot_documents
            );
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            DELETE FROM documents
            WHERE document_id NOT IN (
                SELECT DISTINCT document_id FROM document_versions
            );
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();
    }

    private HashSet<string> GetPinnedSnapshotIds(string workspaceId, SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT pinned_snapshot_id FROM snapshot_pins WHERE workspace_id = @workspaceId;";
        cmd.Parameters.AddWithValue("@workspaceId", workspaceId);
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            set.Add(reader.GetString(0));
        return set;
    }

    private bool IsPinnedSnapshot(string snapshotId, SqliteTransaction transaction)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = SnapshotPinStore.IsPinnedSnapshotSql;
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        return cmd.ExecuteScalar() != null;
    }
}
