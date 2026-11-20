// Purpose: literal/exact-text search over source content.
// Owns: byte-exact substring search with line/column reporting and keyset pagination.
// Must not contain: Roslyn dependency.

using System.Text;
using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class TextSearchStore
{
    private readonly SqliteConnection _connection;

    public TextSearchStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        TextCaseMatching.Register(_connection);
    }

    /// <summary>
    ///     The candidate query of <see cref="SearchTextPage" />. Case-sensitive uses instr;
    ///     case-insensitive uses the registered OrdinalIgnoreCase SQLite function (D184.4).
    ///     One owner: the plan tests pin this text.
    /// </summary>
    internal static string CandidateSql(bool includeGenerated, bool ignoreCase)
    {
        var filter = includeGenerated
            ? ""
            : GeneratedDocumentFilter.ExcludeGeneratedDocuments("dv.document_version_id");
        var contains = ignoreCase
            ? $" AND {TextCaseMatching.SqlFunctionName}(CAST(dv.content AS TEXT), @query)"
            : " AND instr(CAST(dv.content AS TEXT), @query) > 0";
        return $"""
            SELECT d.relative_path, dv.content, dv.line_starts, dv.document_version_id
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND dv.content IS NOT NULL{filter}{contains};
            """;
    }

    public TextSearchPage SearchTextPage(string query, string snapshotId, int limit, bool includeGenerated, bool ignoreCase, TextSearchCursor? cursor)
    {
        if (string.IsNullOrEmpty(query) || limit <= 0)
            return new TextSearchPage([], null, 0);

        var fingerprint = TextSearchCursor.ComputeFingerprint(query, includeGenerated, ignoreCase);
        cursor?.Validate(snapshotId, fingerprint);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // Fetch candidate documents that contain the literal. Push the literal containment
        // into SQL to avoid fetching every document's content.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = CandidateSql(includeGenerated, ignoreCase);

        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@query", query);

        var candidates = new List<(string Path, byte[] Bytes, int[] LineStarts, string DocumentVersionId)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var path = reader.GetString(0);
                var bytes = (byte[])reader[1];
                var lineStartsJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                var documentVersionId = reader.GetString(3);
                var lineStarts = SourceLineMap.ParseLineStarts(lineStartsJson, documentVersionId)
                    ?? throw new InvalidOperationException($"Missing line_starts for document version '{documentVersionId}'.");
                candidates.Add((path, bytes, lineStarts, documentVersionId));
            }
        }

        // Order candidates by Ordinal path to match C# cursor comparisons (D184.5).
        candidates.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));

        // Expand to per-occurrence results in deterministic order: path ascending, offset ascending.
        var all = new List<TextSearchResult>();
        foreach (var (path, bytes, lineStarts, documentVersionId) in candidates)
        {
            var content = Encoding.UTF8.GetString(bytes);
            var occs = FindOccurrences(content, query, comparison);
            var charPos = 0;
            var bytePos = 0;
            foreach (var offset in occs)
            {
                bytePos += Encoding.UTF8.GetByteCount(content.AsSpan(charPos, offset - charPos));
                charPos = offset;
                var startByteOffset = bytePos;

                var endOffset = offset + query.Length;
                bytePos += Encoding.UTF8.GetByteCount(content.AsSpan(charPos, endOffset - charPos));
                charPos = endOffset;
                var endByteOffset = bytePos;

                var startLineIndex = SourceLineMap.FindLineIndex(lineStarts, startByteOffset);
                var startLine = LineNumbers.ToOneBased(startLineIndex);
                var startColumn = SourceLineMap.Utf8Column(bytes, lineStarts[startLineIndex], startByteOffset);

                var endLineIndex = SourceLineMap.FindLineIndex(lineStarts, endByteOffset);
                var endLine = LineNumbers.ToOneBased(endLineIndex);
                var endColumn = SourceLineMap.Utf8Column(bytes, lineStarts[endLineIndex], endByteOffset);

                var lineText = SourceLineMap.ExtractLineText(bytes, lineStarts, startLineIndex, documentVersionId);
                all.Add(new TextSearchResult(path, startLine, startColumn, endLine, endColumn, lineText, offset));
            }
        }

        // Keyset pagination: start at the first result strictly after the cursor key (D184.5).
        var startIndex = 0;
        if (cursor != null)
        {
            startIndex = all.Count;
            for (var i = 0; i < all.Count; i++)
            {
                if (IsAfterCursor(all[i], cursor))
                {
                    startIndex = i;
                    break;
                }
            }
        }

        var totalCount = all.Count;
        var pageItems = all.Skip(startIndex).Take(limit + 1).ToList();

        string? nextCursor = null;
        if (pageItems.Count > limit)
        {
            pageItems.RemoveAt(pageItems.Count - 1);
            var last = pageItems[^1];
            nextCursor = new TextSearchCursor(snapshotId, fingerprint, last.DocumentPath, last.StartOffset).Encode();
        }

        return new TextSearchPage(pageItems, nextCursor, totalCount);
    }

    private static bool IsAfterCursor(TextSearchResult result, TextSearchCursor cursor)
    {
        var pathComparison = string.CompareOrdinal(result.DocumentPath, cursor.LastDocumentPath);
        return pathComparison > 0 || (pathComparison == 0 && result.StartOffset > cursor.LastOffset);
    }

    private static List<int> FindOccurrences(string content, string query, StringComparison comparison)
    {
        var offsets = new List<int>();
        if (string.IsNullOrEmpty(query))
            return offsets;
        var idx = 0;
        while (idx <= content.Length - query.Length)
        {
            var found = content.IndexOf(query, idx, comparison);
            if (found < 0)
                break;
            offsets.Add(found);
            // Non-overlapping occurrences: resume after the matched span (D66).
            idx = found + query.Length;
        }
        return offsets;
    }
}
