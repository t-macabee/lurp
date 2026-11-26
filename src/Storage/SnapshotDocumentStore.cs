using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;

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

    internal bool DocumentExists(string relativePath, string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM snapshot_documents sd
              JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
              JOIN documents d ON d.document_id = dv.document_id
              WHERE sd.snapshot_id = @snapshotId AND d.relative_path = @relativePath);
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@relativePath", relativePath);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    internal string? GetSource(string relativePath, string snapshotId)
    {
        var slice = GetSourceSlice(relativePath, snapshotId, null, null, null);
        return slice?.Source;
    }

    internal SourceSlice? GetSourceSlice(string relativePath, string snapshotId, int? startLine, int? endLine, int? contextLines)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT dv.content, dv.line_starts, dv.document_version_id
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
        var documentVersionId = reader.GetString(2);

        var lineStarts = SourceLineMap.ParseLineStarts(lineStartsJson, documentVersionId)
            ?? throw new InvalidOperationException($"Missing line_starts for document version '{documentVersionId}'.");

        var totalLines = lineStarts.Length;

        // No window requested — return whole file verbatim (back-compatible).
        if (startLine == null && endLine == null && contextLines == null)
        {
            var whole = Encoding.UTF8.GetString(bytes);
            return new SourceSlice(whole, 1, totalLines, totalLines, false);
        }

        // Resolve requested range: defaults to whole file when one bound is absent.
        var requestedStart = startLine ?? 1;
        var requestedEnd = endLine ?? totalLines;

        // Validation already done by caller for positive values; clamp defensively.
        if (requestedStart < 1) requestedStart = 1;
        if (requestedEnd < 1) requestedEnd = 1;
        if (requestedStart > totalLines)
            throw new SourceLineOutOfRangeException(requestedStart, totalLines);
        if (requestedEnd > totalLines)
            requestedEnd = totalLines;

        var ctx = contextLines ?? 0;
        if (ctx < 0) ctx = 0;

        var (byteStart, byteEnd, firstLineIndex, lastLineIndex) = SourceLineMap.ExpandLineWindow(
            lineStarts, requestedStart - 1, requestedEnd - 1, ctx, bytes.Length);
        var sliced = SourceLineMap.SliceUtf8(bytes, byteStart, byteEnd, documentVersionId);

        var expandedStart = LineNumbers.ToOneBased(firstLineIndex);
        var expandedEnd = LineNumbers.ToOneBased(lastLineIndex);
        var truncated = expandedStart != 1 || expandedEnd != totalLines;
        return new SourceSlice(sliced, expandedStart, expandedEnd, totalLines, truncated);
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

    internal (HashSet<string> DocumentPaths, HashSet<string> GeneratedDocumentPaths) GetSnapshotDocumentPaths(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.relative_path, dv.is_generated
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        var documentPaths = new HashSet<string>(StringComparer.Ordinal);
        var generatedPaths = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            documentPaths.Add(path);
            if (reader.GetInt32(1) != 0)
                generatedPaths.Add(path);
        }

        return (documentPaths, generatedPaths);
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
              AND d.relative_path IN (SELECT value FROM json_each(@documentPaths));
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@documentPaths", pathList);
        var results = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    internal int CountDocumentsInSnapshot(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM snapshot_documents WHERE snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}