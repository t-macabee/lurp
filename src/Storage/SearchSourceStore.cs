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
        return results;
    }
}
