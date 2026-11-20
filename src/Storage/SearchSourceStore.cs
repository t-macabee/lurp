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
        TextCaseMatching.Register(_connection);
    }

    /// <summary>
    ///     The <c>matches</c> CTE body of <see cref="SearchSource" />, with or without the
    ///     generated-document filter. One owner: the plan tests pin this text.
    /// </summary>
    internal static string FtsMatchSql(bool includeGenerated)
    {
        var filter = includeGenerated
            ? ""
            : GeneratedDocumentFilter.ExcludeGeneratedDocuments("source_fts.document_version_id");
        return $"""
                SELECT source_fts.rowid,
                       source_fts.rank,
                       ROW_NUMBER() OVER (
                           PARTITION BY source_fts.document_path
                           ORDER BY source_fts.rank
                       ) AS path_rank
                FROM source_fts
                WHERE source_fts MATCH @query
                  AND source_fts.snapshot_id = @snapshotId{filter}
            """;
    }

    /// <summary>
    ///     The fragment fallback query of <see cref="SearchSource" />. One owner: the plan tests
    ///     pin this text.
    /// </summary>
    internal static string FallbackSql(bool includeGenerated)
    {
        var filter = includeGenerated
            ? ""
            : GeneratedDocumentFilter.ExcludeGeneratedDocuments("dv.document_version_id");
        return $"""
            SELECT d.relative_path, CAST(dv.content AS TEXT)
            FROM document_versions dv
            JOIN documents d ON d.document_id = dv.document_id
            JOIN snapshot_documents sd ON sd.document_version_id = dv.document_version_id
            WHERE sd.snapshot_id = @snapshotId
              AND dv.content IS NOT NULL
              AND {TextCaseMatching.SqlFunctionName}(CAST(dv.content AS TEXT), @query)
              AND d.relative_path NOT IN (SELECT value FROM json_each(@excludedPaths)){filter}
            ORDER BY d.relative_path
            LIMIT @remaining
            """;
    }

    /// <summary>Searches the snapshot's source text; see <see cref="SqliteIndexStore.SearchSource" />.</summary>
    public List<SourceSearchResult> SearchSource(string query, string snapshotId, int limit = 20, bool includeGenerated = false, int snippetTokens = 64)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        snippetTokens = Math.Max(1, snippetTokens);
        var contentFtsQuery = SearchUtils.ToColumnFtsPhrase("content", query);

        var ftsRows = new List<(long RowId, string DocumentPath)>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "WITH matches AS (\n" + FtsMatchSql(includeGenerated) + """
                )
                SELECT source_fts.rowid,
                       source_fts.document_path
                FROM source_fts
                JOIN matches ON matches.rowid = source_fts.rowid
                WHERE matches.path_rank = 1
                ORDER BY matches.rank
                LIMIT @limit;
                """;

            command.Parameters.AddWithValue("@query", contentFtsQuery);
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
                ftsRows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        var results = new List<SourceSearchResult>(ftsRows.Count);
        if (ftsRows.Count > 0)
        {
            using var highlightCmd = _connection.CreateCommand();
            highlightCmd.CommandText = """
                SELECT highlight(source_fts, 1, char(2), char(3))
                FROM source_fts
                WHERE source_fts MATCH @query
                  AND rowid = @rowid;
                """;
            var queryParam = highlightCmd.Parameters.Add("@query", SqliteType.Text);
            queryParam.Value = contentFtsQuery;
            var rowIdParam = highlightCmd.Parameters.Add("@rowid", SqliteType.Integer);

            foreach (var (rowId, docPath) in ftsRows)
            {
                rowIdParam.Value = rowId;
                var highlighted = highlightCmd.ExecuteScalar() as string ?? "";
                var snippet = SnippetWindowBuilder.BuildFromHighlighted(highlighted, snippetTokens);
                results.Add(new SourceSearchResult(docPath, snippet));
            }
        }

        if (results.Count < limit && SearchUtils.IsPlainIdentifierQuery(query))
        {
            var remaining = limit - results.Count;
            using var fbCmd = _connection.CreateCommand();
            fbCmd.CommandText = FallbackSql(includeGenerated);
            fbCmd.Parameters.AddWithValue("@query", query);
            fbCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            SqlIdList.Bind(fbCmd, "@excludedPaths", results.Select(static r => r.DocumentPath));
            fbCmd.Parameters.AddWithValue("@remaining", remaining);

            using var fbReader = fbCmd.ExecuteReader();
            while (fbReader.Read())
            {
                var docPath = fbReader.GetString(0);
                var content = fbReader.IsDBNull(1) ? "" : fbReader.GetString(1);
                var snippet = SnippetWindowBuilder.BuildFromPlainText(content, query, snippetTokens);
                results.Add(new SourceSearchResult(docPath, snippet));
            }
        }

        return results;
    }
}