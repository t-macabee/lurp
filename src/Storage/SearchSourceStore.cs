// Purpose: source half of the search store.
// Owns: FTS5 source-text search and snippet windowing.
// Must not contain: the other halves, or any Roslyn dependency.

using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SearchSourceStore
{
    private readonly SqliteConnection _connection;

    public SearchSourceStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc cref="ISearchStore.SearchSource"/>
    public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        limit = Math.Max(1, limit);
        snippetTokens = Math.Max(1, snippetTokens);

        using var command = _connection.CreateCommand();

        command.CommandText = @"
            WITH matches AS (
                SELECT source_fts.rowid,
                       source_fts.rank,
                       ROW_NUMBER() OVER (
                           PARTITION BY source_fts.document_path
                           ORDER BY source_fts.rank
                       ) AS path_rank
                FROM source_fts
                WHERE source_fts MATCH @query
                  AND source_fts.snapshot_id = @snapshotId
        ";

        if (!includeGenerated)
        {
            command.CommandText += @"
                  AND NOT EXISTS (
                      SELECT 1
                      FROM declarations dec
                      WHERE dec.document_version_id = source_fts.document_version_id
                        AND dec.is_generated = 1
                  )";
        }

        command.CommandText += @"
            )
            SELECT source_fts.document_path,
                   snippet(source_fts, 1, '<mark>', '</mark>', '…', @snippetTokens) AS snippet
            FROM source_fts
            JOIN matches ON matches.rowid = source_fts.rowid
            WHERE matches.path_rank = 1
            ORDER BY matches.rank
            LIMIT @limit;
        ";

        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@snippetTokens", snippetTokens);

        var results = new List<SourceSearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new SourceSearchResult(documentPath: reader.GetString(0),
                snippet: reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }

        // Architecture §10 "identifier fragments" contract: FTS5 unicode61 index
        // matches whole tokens only, missing camelCase substrings. When FTS5
        // saturates fewer than the requested limit, fall back to LIKE over
        // document content for plain identifier queries.
        if (results.Count < limit && IsPlainIdentifierQuery(query))
        {
            var remaining = limit - results.Count;
            var seen = new HashSet<string>(results.Select(static r => r.DocumentPath));
            using var fbCmd = _connection.CreateCommand();
            fbCmd.CommandText = @"
                SELECT d.relative_path, CAST(dv.content AS TEXT)
                FROM document_versions dv
                JOIN documents d ON d.document_id = dv.document_id
                JOIN snapshot_documents sd ON sd.document_version_id = dv.document_version_id
                WHERE sd.snapshot_id = @snapshotId
                  AND dv.content IS NOT NULL
                  AND CAST(dv.content AS TEXT) LIKE @likePattern ESCAPE '\'";
            if (!includeGenerated)
            {
                fbCmd.CommandText += @"
                  AND NOT EXISTS (
                      SELECT 1 FROM declarations dec
                      WHERE dec.document_version_id = dv.document_version_id
                        AND dec.is_generated = 1
                  )";
            }
            fbCmd.CommandText += @"
                ORDER BY d.relative_path
                LIMIT @remaining";

            var escaped = query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            fbCmd.Parameters.AddWithValue("@likePattern", $"%{escaped}%");
            fbCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            fbCmd.Parameters.AddWithValue("@remaining", remaining);
            using var fbReader = fbCmd.ExecuteReader();
            while (fbReader.Read())
            {
                var docPath = fbReader.GetString(0);
                if (!seen.Add(docPath))
                    continue;
                var content = fbReader.IsDBNull(1) ? "" : fbReader.GetString(1);
                var snippet = TruncateSnippet(content, query);
                results.Add(new SourceSearchResult(documentPath: docPath, snippet: snippet));
            }
        }

        return results;
    }

    private static bool IsPlainIdentifierQuery(string query)
    {
        foreach (var c in query)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '_'))
                return false;
        }
        return true;
    }

    private static string TruncateSnippet(string content, string query)
    {
        if (string.IsNullOrEmpty(content))
            return "";
        var idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return content.Length <= 320 ? content : content[..317] + "...";
        var start = Math.Max(0, idx - 80);
        var end = Math.Min(content.Length, idx + query.Length + 240);
        var snippet = (start > 0 ? "…" : "") + content[start..end] + (end < content.Length ? "…" : "");
        return snippet;
    }
}
