using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace Lurp.Storage;

public sealed record SourceSlice(
    string Source,
    int StartLine,
    int EndLine,
    int TotalLines,
    bool Truncated);

internal sealed class SnapshotDocumentStore(SqliteConnection connection)
{
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal string? GetSource(string relativePath, string snapshotId)
    {
        var slice = GetSourceSlice(relativePath, snapshotId, null, null, null);
        return slice?.Source;
    }

    internal SourceSlice? GetSourceSlice(string relativePath, string snapshotId, int? startLine, int? endLine, int? contextLines)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT dv.content, dv.line_starts
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE d.relative_path = @relativePath
              AND sd.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@relativePath", relativePath);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        if (reader.IsDBNull(0))
            return null;

        var bytes = (byte[])reader[0];
        var lineStartsJson = reader.IsDBNull(1) ? null : reader.GetString(1);

        // No window requested — return whole file verbatim (back-compatible).
        if (startLine == null && endLine == null && contextLines == null)
        {
            var whole = Encoding.UTF8.GetString(bytes);
            var totalLinesWhole = TryParseLineStarts(lineStartsJson, out var lsWhole) ? lsWhole.Length : CountLinesFallback(bytes);
            return new SourceSlice(whole, 1, totalLinesWhole, totalLinesWhole, false);
        }

        // If line_starts is missing, fall back to whole file (cannot slice safely).
        if (!TryParseLineStarts(lineStartsJson, out var lineStarts) || lineStarts.Length == 0)
        {
            var whole = Encoding.UTF8.GetString(bytes);
            var totalLinesFallback = CountLinesFallback(bytes);
            return new SourceSlice(whole, 1, totalLinesFallback, totalLinesFallback, false);
        }

        var totalLines = lineStarts.Length;

        // Resolve requested range: defaults to whole file when one bound is absent.
        var requestedStart = startLine ?? 1;
        var requestedEnd = endLine ?? totalLines;

        // Validation already done by caller for positive values; clamp defensively.
        if (requestedStart < 1) requestedStart = 1;
        if (requestedEnd < 1) requestedEnd = 1;
        if (requestedStart > totalLines)
            throw new ArgumentOutOfRangeException(nameof(startLine), $"start_line {requestedStart} is beyond total_lines {totalLines}.");
        if (requestedEnd > totalLines)
            requestedEnd = totalLines;
        if (requestedStart > requestedEnd)
            throw new ArgumentException($"start_line {requestedStart} must be <= end_line {requestedEnd}.");

        var ctx = contextLines ?? 0;
        if (ctx < 0) ctx = 0;

        var expandedStart = Math.Max(1, requestedStart - ctx);
        var expandedEnd = Math.Min(totalLines, requestedEnd + ctx);

        var byteStart = lineStarts[expandedStart - 1];
        var byteEnd = expandedEnd < totalLines ? lineStarts[expandedEnd] : bytes.Length;

        var sliced = SliceToString(bytes, byteStart, byteEnd);
        if (sliced == null)
            throw new InvalidOperationException($"Failed to slice document '{relativePath}' at lines {expandedStart}-{expandedEnd}.");

        var truncated = expandedStart != 1 || expandedEnd != totalLines;
        return new SourceSlice(sliced, expandedStart, expandedEnd, totalLines, truncated);
    }

    private static bool TryParseLineStarts(string? json, out int[] lineStarts)
    {
        lineStarts = [];
        if (string.IsNullOrEmpty(json))
            return false;
        try
        {
            var parsed = JsonSerializer.Deserialize<int[]>(json);
            if (parsed is { Length: > 0 })
            {
                lineStarts = parsed;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static int CountLinesFallback(byte[] bytes)
    {
        if (bytes.Length == 0) return 0;
        var count = 1;
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n')
                count++;
        return count;
    }

    private static string? SliceToString(byte[] content, int start, int end)
    {
        if (start < 0 || end > content.Length || start > end)
            return null;
        var length = end - start;
        if (length == 0)
            return string.Empty;
        return Encoding.UTF8.GetString(content, start, length);
    }

    internal void SaveSnapshotDocuments(string snapshotId, IEnumerable<(string DocumentId, string DocumentVersionId)> entries)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            foreach (var (_, versionId) in entries)
            {
                command.CommandText = """
                    INSERT OR IGNORE INTO snapshot_documents (snapshot_id, document_version_id)
                    VALUES (@snapshotId, @documentVersionId);
                    """;
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@snapshotId", snapshotId);
                command.Parameters.AddWithValue("@documentVersionId", versionId);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    internal Dictionary<string, string> GetDocumentVersionIdsByPath(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.relative_path, dv.document_version_id
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var result = new Dictionary<string, string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    internal List<string> GetDocumentVersionIdsForDocuments(string snapshotId, IEnumerable<string> documentPaths)
    {
        var pathList = documentPaths as IReadOnlyCollection<string> ?? [.. documentPaths];
        if (pathList.Count == 0)
            return [];

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT dv.document_version_id
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId
              AND d.relative_path IN (
            """ + string.Join(", ", pathList.Select((_, i) => $"@p{i}")) + """
        );
        """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var i = 0;
        foreach (var path in pathList)
            command.Parameters.AddWithValue($"@p{i++}", path);
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }
}