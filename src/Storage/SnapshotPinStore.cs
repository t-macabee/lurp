using Microsoft.Data.Sqlite;
using System.Globalization;

namespace Lurp.Storage;

internal sealed class SnapshotPinStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    // The one "complete pin" rule: the pin points at a snapshot row that exists, has a
    // build time and is complete. Every pin selection states it with this predicate.
    private const string CompletePinPredicate =
        "s.snapshot_id IS NOT NULL AND s.built_at_utc IS NOT NULL AND s.status = @completeStatus";

    private SqliteCommand BuildPinCommand(string? workspaceId, bool complete)
    {
        var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT sp.workspace_id, sp.pinned_snapshot_id, sp.pinned_at_utc, sp.previous_pinned_snapshot_id, s.built_at_utc
            FROM snapshot_pins sp
            LEFT JOIN snapshots s ON s.snapshot_id = sp.pinned_snapshot_id
            WHERE (@workspaceId IS NULL OR sp.workspace_id = @workspaceId)
              AND {(complete ? CompletePinPredicate : $"NOT COALESCE(({CompletePinPredicate}), 0)")}
            ORDER BY sp.pinned_at_utc DESC, sp.workspace_id ASC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@workspaceId", string.IsNullOrEmpty(workspaceId) ? DBNull.Value : workspaceId);
        command.Parameters.AddWithValue("@completeStatus", SnapshotStatusValues.Complete);
        return command;
    }

    /// <summary>Returns the active pin, or <c>null</c> when there is no pin or the pinned snapshot is stale.</summary>
    internal PinnedSnapshotRow? GetPinnedSnapshot(string? workspaceId = null)
    {
        using var command = BuildPinCommand(workspaceId, complete: true);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var ws = reader.GetString(0);
        var pinnedId = reader.GetString(1);
        var pinnedAt = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var prev = reader.IsDBNull(3) ? null : reader.GetString(3);
        var builtAt = DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        // A pin whose snapshot is missing or is not complete is treated as absent; the
        // completeness test is the SQL predicate above. Reads never write (R149): only
        // ClearStalePin removes a stale pin, and only the three write commands call it.
        return new PinnedSnapshotRow(ws, pinnedId, pinnedAt, prev, builtAt);
    }

    /// <summary>
    ///     The one stale-pin rule: the most recently set pin whose snapshot is missing
    ///     or is not complete, with the workspace that holds it, or <c>null</c>.
    /// </summary>
    private (string WorkspaceId, string PinnedSnapshotId)? ReadStalePin(string? workspaceId, SqliteTransaction? transaction = null)
    {
        using var command = BuildPinCommand(workspaceId, complete: false);
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    ///     Returns the pinned snapshot id when that snapshot is missing or is not
    ///     complete, without clearing the pin. Returns <c>null</c> when there is no
    ///     pin or the pinned snapshot is complete.
    /// </summary>
    internal string? GetStalePinnedSnapshotId(string? workspaceId = null)
    {
        return ReadStalePin(workspaceId)?.PinnedSnapshotId;
    }

    /// <summary>
    ///     Deletes the stale pin that <see cref="GetStalePinnedSnapshotId" /> reports and
    ///     returns its id. Returns <c>null</c> when there is no pin or the pinned
    ///     snapshot is complete. This is the only way a stale pin is removed; every
    ///     read path treats such a pin as absent and leaves the row alone.
    /// </summary>
    internal string? ClearStalePin(string? workspaceId = null)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);
        var stale = ReadStalePin(workspaceId, transaction);
        if (stale == null)
        {
            transaction.Commit();
            return null;
        }

        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM snapshot_pins WHERE workspace_id = @workspaceId AND pinned_snapshot_id = @pinnedSnapshotId;";
        command.Parameters.AddWithValue("@workspaceId", stale.Value.WorkspaceId);
        command.Parameters.AddWithValue("@pinnedSnapshotId", stale.Value.PinnedSnapshotId);
        command.ExecuteNonQuery();
        transaction.Commit();
        return stale.Value.PinnedSnapshotId;
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
}
