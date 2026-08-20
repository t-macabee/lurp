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
    }

    public TextSearchPage SearchTextPage(string query, string snapshotId, int limit, bool includeGenerated, bool ignoreCase, TextSearchCursor? cursor)
    {
        if (string.IsNullOrEmpty(query) || limit <= 0)
            return new TextSearchPage([], null, 0);

        limit = Math.Max(1, limit);
        var fingerprint = TextSearchCursor.ComputeFingerprint(query, includeGenerated, ignoreCase);
        cursor?.Validate(snapshotId, fingerprint);

        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // Fetch candidate documents that contain the literal. Use SQL instr() to reduce the set
        // before decoding in C#. For case-sensitive, instr is exact; for insensitive, lower() both sides.
        using var cmd = _connection.CreateCommand();
        var where = "WHERE sd.snapshot_id = @snapshotId AND dv.content IS NOT NULL";
        if (!includeGenerated)
            where += " AND NOT EXISTS (SELECT 1 FROM declarations dec WHERE dec.document_version_id = dv.document_version_id AND dec.is_generated = 1)";

        // Push the literal containment into SQL to avoid fetching every document's content.
        // instr returns 0 when not found, >0 when found. For ignoreCase we compare lowercased content.
        if (ignoreCase)
            where += " AND instr(lower(CAST(dv.content AS TEXT)), lower(@query)) > 0";
        else
            where += " AND instr(CAST(dv.content AS TEXT), @query) > 0";

        cmd.CommandText = $"""
            SELECT d.relative_path, dv.content
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            {where}
            ORDER BY d.relative_path;
            """;

        cmd.Parameters.AddWithValue("@snapshotId", snapshotId);
        cmd.Parameters.AddWithValue("@query", query);

        var candidates = new List<(string Path, string Content)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var path = reader.GetString(0);
                byte[] bytes = (byte[])reader[1];
                var content = Encoding.UTF8.GetString(bytes);
                candidates.Add((path, content));
            }
        }

        // Expand to per-occurrence results in deterministic order: path ascending, offset ascending.
        var all = new List<TextSearchResult>();
        foreach (var (path, content) in candidates)
        {
            // Precompute line break positions for fast line/column lookup? For now scan per occurrence;
            // candidate count is small (typically tens of docs), occurrences per doc moderate.
            var occs = FindOccurrences(content, query, comparison);
            foreach (var offset in occs)
            {
                var (startLine, startColumn) = GetLineColumn(content, offset);
                var endOffset = offset + query.Length;
                var (endLine, endColumn) = GetLineColumn(content, endOffset);
                var lineText = ExtractLineText(content, offset);
                all.Add(new TextSearchResult(path, startLine, startColumn, endLine, endColumn, lineText, offset));
            }
        }

        // Keyset pagination: skip past cursor
        var startIndex = 0;
        if (cursor != null)
        {
            // Find position after the cursor's last item
            var found = false;
            for (var i = 0; i < all.Count; i++)
            {
                var r = all[i];
                if (string.Equals(r.DocumentPath, cursor.LastDocumentPath, StringComparison.Ordinal) && r.StartOffset == cursor.LastOffset)
                {
                    startIndex = i + 1;
                    found = true;
                    break;
                }
                // If cursor's document path is lexicographically before current result, start here
                if (string.CompareOrdinal(r.DocumentPath, cursor.LastDocumentPath) > 0)
                {
                    // The cursor document was not in the result set? This happens if the cursor's document
                    // no longer matches the query (should not happen for immutable snapshot). Treat as start at this index.
                    // But we already scan linearly; if cursor path < current path, we have passed the cursor's position.
                    // To handle missing cursor due to stale filtering, we fall through to binary search alternative.
                    // For determinism, we locate the first result strictly after cursor ordering.
                    // Ordering is path ascending then offset ascending.
                    // So if r.Path > cursor.Path, then r is after cursor regardless of offset.
                    startIndex = i;
                    found = true;
                    break;
                }
                if (string.Equals(r.DocumentPath, cursor.LastDocumentPath, StringComparison.Ordinal) && r.StartOffset > cursor.LastOffset)
                {
                    startIndex = i;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                // Cursor points past the end -> empty page
                startIndex = all.Count;
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

    /// <summary>
    ///     Convenience non-paginated overload: returns at most <paramref name="limit"/> results (first page).
    /// </summary>
    public List<TextSearchResult> SearchText(string query, string snapshotId, int limit = 20, bool includeGenerated = false, bool ignoreCase = false)
    {
        var page = SearchTextPage(query, snapshotId, limit, includeGenerated, ignoreCase, null);
        return page.Items;
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
            // Non-overlapping advance; use +1 to allow overlapping matches would risk infinite loop on e.g. "aa" in "aaa" (overlapping finds 0,1).
            // For grep-style, non-overlapping with +query.Length is expected, but overlapping detection is more correct for exact search.
            // Choose +1 to report overlapping occurrences (e.g., "aa" in "aaa" finds 2). This matches GNU grep -o behavior.
            idx = found + 1;
            // Guard against zero-length query (already filtered) and ensure progress
            if (idx <= found)
                idx = found + 1;
        }
        return offsets;
    }

    private static (int Line, int Column) GetLineColumn(string content, int offset)
    {
        // offset is exclusive end for endLine/endColumn; for start it is inclusive start.
        // Lines are 1-based, columns 0-based.
        // Clamp offset to content length
        if (offset < 0) offset = 0;
        if (offset > content.Length) offset = content.Length;

        var line = 1;
        var lastNewline = -1;
        for (var i = 0; i < offset; i++)
        {
            if (content[i] == '\n')
            {
                line++;
                lastNewline = i;
            }
        }
        var column = offset - lastNewline - 1;
        if (column < 0) column = 0;
        return (line, column);
    }

    private static string ExtractLineText(string content, int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > content.Length) offset = content.Length;

        // Find line start: after previous '\n' or 0
        var lineStart = offset;
        while (lineStart > 0 && content[lineStart - 1] != '\n')
            lineStart--;

        // Find line end: next '\n' or end of content
        var lineEnd = offset;
        while (lineEnd < content.Length && content[lineEnd] != '\n')
            lineEnd++;

        var line = content[lineStart..lineEnd];
        // Strip trailing '\r' from \r\n sequences
        if (line.EndsWith("\r", StringComparison.Ordinal))
            line = line[..^1];
        return line;
    }
}
