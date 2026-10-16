using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Lurp.Storage;

internal sealed class SnapshotPinStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    private SqliteCommand BuildPinCommand(string? workspaceId)
    {
        var command = _connection.CreateCommand();
        if (!string.IsNullOrEmpty(workspaceId))
        {
            command.CommandText = """
                SELECT sp.workspace_id, sp.pinned_snapshot_id, sp.pinned_at_utc, sp.previous_pinned_snapshot_id, s.built_at_utc
                FROM snapshot_pins sp
                LEFT JOIN snapshots s ON s.snapshot_id = sp.pinned_snapshot_id
                WHERE sp.workspace_id = @workspaceId;
                """;
            command.Parameters.AddWithValue("@workspaceId", workspaceId);
        }
        else
        {
            // When workspaceId not supplied, if DB has single workspace return its pin,
            // otherwise prefer single pin if only one exists.
            command.CommandText = """
                SELECT sp.workspace_id, sp.pinned_snapshot_id, sp.pinned_at_utc, sp.previous_pinned_snapshot_id, s.built_at_utc
                FROM snapshot_pins sp
                LEFT JOIN snapshots s ON s.snapshot_id = sp.pinned_snapshot_id
                LIMIT 1;
                """;
        }

        return command;
    }

    internal PinnedSnapshotRow? GetPinnedSnapshot(string? workspaceId = null)
    {
        using var command = BuildPinCommand(workspaceId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var ws = reader.GetString(0);
        var pinnedId = reader.GetString(1);
        var pinnedAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var prev = reader.IsDBNull(3) ? null : reader.GetString(3);
        DateTime? builtAt = null;
        if (!reader.IsDBNull(4))
            builtAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        // Validate that pinned snapshot still exists and is complete; if not, self-heal by clearing.
        if (builtAt == null || !IsCompleteSnapshot(pinnedId))
        {
            // Stale pin: remove it and return null so caller falls back to built_at latest.
            // A read-only connection (status, MCP reads) cannot clear the pin; the caller still falls back to the latest snapshot.
            try { ClearPinnedSnapshot(ws); }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SQLitePCL.raw.SQLITE_READONLY) { }
            return null;
        }

        return new PinnedSnapshotRow(ws, pinnedId, pinnedAt, prev, builtAt);
    }

    /// <summary>
    ///     Returns the pinned snapshot id when that snapshot is missing or is not
    ///     complete, without clearing the pin. Returns <c>null</c> when there is no
    ///     pin or the pinned snapshot is complete.
    /// </summary>
    internal string? GetStalePinnedSnapshotId(string? workspaceId = null)
    {
        using var command = BuildPinCommand(workspaceId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var pinnedId = reader.GetString(1);
        var snapshotMissing = reader.IsDBNull(4);
        return snapshotMissing || !IsCompleteSnapshot(pinnedId) ? pinnedId : null;
    }

    internal string? GetPinnedSnapshotId(string? workspaceId = null)
    {
        return GetPinnedSnapshot(workspaceId)?.PinnedSnapshotId;
    }

    internal void SetPinnedSnapshot(string snapshotId)
    {
        if (string.IsNullOrWhiteSpace(snapshotId))
            throw new ArgumentException("snapshotId is required.", nameof(snapshotId));

        // Resolve workspace_id and status for this snapshot.
        string? workspaceId;
        string? status;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT workspace_id, status FROM snapshots WHERE snapshot_id = @snapshotId;";
            cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"ERROR: snapshot '{snapshotId}' not found.");
            workspaceId = reader.GetString(0);
            status = reader.GetString(1);
        }

        if (!string.Equals(status, SnapshotStatusValues.Complete, StringComparison.Ordinal))
            throw new InvalidOperationException($"ERROR: snapshot '{snapshotId}' has status '{status}' and cannot be pinned. Only snapshots with status 'complete' can be pinned.");

        // Fetch previous pin to preserve audit trail.
        string? previousPinned = null;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT pinned_snapshot_id FROM snapshot_pins WHERE workspace_id = @workspaceId;";
            cmd.Parameters.AddWithValue("@workspaceId", workspaceId);
            previousPinned = cmd.ExecuteScalar() as string;
        }

        // If re-pinning same id, just update pinned_at.
        using var upsert = _connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO snapshot_pins (workspace_id, pinned_snapshot_id, pinned_at_utc, previous_pinned_snapshot_id)
            VALUES (@workspaceId, @pinnedSnapshotId, @pinnedAtUtc, @previousPinnedSnapshotId)
            ON CONFLICT(workspace_id) DO UPDATE SET
                pinned_snapshot_id = excluded.pinned_snapshot_id,
                pinned_at_utc = excluded.pinned_at_utc,
                previous_pinned_snapshot_id = excluded.previous_pinned_snapshot_id;
            """;
        upsert.Parameters.AddWithValue("@workspaceId", workspaceId);
        upsert.Parameters.AddWithValue("@pinnedSnapshotId", snapshotId);
        upsert.Parameters.AddWithValue("@pinnedAtUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        upsert.Parameters.AddWithValue("@previousPinnedSnapshotId", (object?)previousPinned ?? DBNull.Value);
        upsert.ExecuteNonQuery();
    }

    internal bool ClearPinnedSnapshot(string? workspaceId = null)
    {
        using var command = _connection.CreateCommand();
        if (!string.IsNullOrEmpty(workspaceId))
        {
            command.CommandText = "DELETE FROM snapshot_pins WHERE workspace_id = @workspaceId;";
            command.Parameters.AddWithValue("@workspaceId", workspaceId);
        }
        else
        {
            command.CommandText = "DELETE FROM snapshot_pins;";
        }
        var affected = command.ExecuteNonQuery();
        return affected > 0;
    }

    private bool IsCompleteSnapshot(string snapshotId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM snapshots WHERE snapshot_id = @snapshotId;";
        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        var status = cmd.ExecuteScalar() as string;
        return string.Equals(status, SnapshotStatusValues.Complete, StringComparison.Ordinal);
    }
}
