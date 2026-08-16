using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SnapshotPruner(SqliteConnection connection)
{
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
        using var snapCmd = _connection.CreateCommand();
        snapCmd.CommandText = """
            SELECT snapshot_id FROM snapshots
            WHERE workspace_id = @workspaceId
            ORDER BY built_at_utc DESC;
            """;
        snapCmd.Parameters.AddWithValue("@workspaceId", workspaceId);

        var snapshotIds = new List<string>();
        using (var snapReader = snapCmd.ExecuteReader())
        {
            while (snapReader.Read())
                snapshotIds.Add(snapReader.GetString(0));
        }

        if (snapshotIds.Count <= keep)
            return;

        var pruneIds = snapshotIds.Skip(keep).ToList();
        if (pruneIds.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
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

    internal void DeleteIncompleteSnapshots()
    {
        using var listCmd = _connection.CreateCommand();
        listCmd.CommandText = """
            SELECT snapshot_id, status
            FROM snapshots
            WHERE status IN (@inProgress, @failed)
              AND payload_pruned = 0;
            """;
        listCmd.Parameters.AddWithValue("@inProgress", SnapshotStatusValues.InProgress);
        listCmd.Parameters.AddWithValue("@failed", SnapshotStatusValues.Failed);
        var snapshotRows = new List<(string SnapshotId, string Status)>();
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
                snapshotRows.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (snapshotRows.Count == 0)
            return;

        using var transaction = _connection.BeginTransaction();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;

            foreach (var (snapshotId, status) in snapshotRows)
            {
                DeleteSnapshotPayload(cmd, snapshotId);
                if (status == SnapshotStatusValues.Failed)
                    // Tombstone: keep the row : and with it the failure reason
                    // that P2-9 exists to expose : but mark the payload pruned so
                    // a later run does not rescan the (now empty) payload.
                    cmd.CommandText = "UPDATE snapshots SET payload_pruned = 1 WHERE snapshot_id = @sid;";
                else
                    // Crashed in-progress rows have no failure reason to preserve;
                    // restore the original cleanup of deleting the whole row.
                    cmd.CommandText = "DELETE FROM snapshots WHERE snapshot_id = @sid;";

                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@sid", snapshotId);
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

    private static void DeleteSnapshotData(SqliteCommand cmd, string snapshotId)
    {
        DeleteSnapshotPayload(cmd, snapshotId);

        cmd.CommandText = "DELETE FROM snapshots WHERE snapshot_id = @sid;";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteSnapshotPayload(SqliteCommand cmd, string snapshotId)
    {
        string[] tables =
        [
            "edges", "diagnostics", "annotations", "snapshot_symbols",
            "projects", "snapshot_documents", "source_fts", "symbol_fts",
            "snapshot_timings", "snapshot_graph_nodes", "binding_incompleteness"
        ];

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

        foreach (var table in tables)
        {
            // Not SQL injection: table is drawn only from the hardcoded literal array
            // above (never external input), and DELETE cannot bind a table name via a
            // parameter in SQL - only values (snapshot_id, bound as @sid) can be.
            cmd.CommandText = $"DELETE FROM {table} WHERE snapshot_id = @sid;";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("@sid", snapshotId);
            cmd.ExecuteNonQuery();
        }

        // Clean up orphaned project references
        cmd.CommandText = """
            DELETE FROM project_references
            WHERE project_id NOT IN (SELECT project_id FROM projects);
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        // Clean up orphaned declarations whose document versions are no longer
        // referenced by any snapshot
        cmd.CommandText = """
            DELETE FROM partial_declarations
            WHERE declaration_id IN (
                SELECT declaration_id FROM declarations
                WHERE document_version_id NOT IN (
                    SELECT DISTINCT document_version_id FROM snapshot_documents
                )
            );
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            DELETE FROM declarations
            WHERE document_version_id NOT IN (
                SELECT DISTINCT document_version_id FROM snapshot_documents
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

        // Foreign-key enforcement is not enabled on the store connection, so
        // explicitly reclaim document data that no retained snapshot references.
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

        // A document can outlive the snapshot recorded in last_changed_snapshot_id
        // when that snapshot is pruned. Repair the pointer to the newest retained
        // snapshot that still references the document, or leave it null when none
        // remains.
        cmd.CommandText = """
            UPDATE documents
            SET last_changed_snapshot_id = (
                SELECT sd.snapshot_id
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN snapshots s ON s.snapshot_id = sd.snapshot_id
                WHERE dv.document_id = documents.document_id
                ORDER BY s.built_at_utc DESC
                LIMIT 1
            )
            WHERE last_changed_snapshot_id IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM snapshot_documents sd
                  JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                  WHERE sd.snapshot_id = documents.last_changed_snapshot_id
                    AND dv.document_id = documents.document_id
              );
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            DELETE FROM graph_nodes
            WHERE node_id NOT IN (
                SELECT DISTINCT node_id FROM snapshot_graph_nodes
            );
            """;
        cmd.Parameters.Clear();
        cmd.ExecuteNonQuery();
    }
}