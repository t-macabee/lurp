// Purpose: symbol half of the search store.
// Owns: symbol FTS/substring search, keyset pagination, and FQN resolution.
// Must not contain: the other halves, or any Roslyn dependency.

using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SearchSymbolStore
{
    private const string ExcludeGeneratedClause = @"
              AND EXISTS (
                    SELECT 1 FROM declarations d
                    JOIN snapshot_documents sd
                      ON sd.snapshot_id = @snapshotId
                     AND sd.document_version_id = d.document_version_id
                    WHERE d.symbol_id = s.symbol_id
                      AND (d.is_generated = 0 OR d.is_generated IS NULL)
                  )";

    private const string FqnOrderLimitClause = " ORDER BY ss.fqn LIMIT 1;";
    private readonly SqliteConnection _connection;

    public SearchSymbolStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <inheritdoc cref="ISearchStore.SearchSymbols" />
    public List<SymbolSearchResult> SearchSymbols(string query, string snapshotId, int limit = 20, bool includeGenerated = false, string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return [];

        limit = Math.Max(1, limit);

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_fts.symbol_id, fqn, doc_comment_id, kind
            FROM symbol_fts
            WHERE symbol_fts MATCH @query
              AND symbol_fts.snapshot_id = @snapshotId
            """;

        if (!string.IsNullOrEmpty(kind))
        {
            command.CommandText += " AND symbol_fts.kind = @kind";
            command.Parameters.AddWithValue("@kind", kind);
        }

        // Snapshot-scoped generated-declaration filter. This is a LEFT JOIN in spirit
        // (symbols with no declaration rows : metadata-only/external symbols : must
        // remain searchable), so it stays a NOT EXISTS / EXISTS disjunction rather
        // than an inner join. Do not collapse this into the single-EXISTS shape used
        // by ResolveSymbolByFqn below : that shape requires at least one declaration
        // and would silently drop external symbols here.
        if (!includeGenerated)
            command.CommandText += @"
              AND (
                    NOT EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = symbol_fts.symbol_id
                    )
                 OR EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = symbol_fts.symbol_id
                          AND (d.is_generated = 0 OR d.is_generated IS NULL)
                    )
                  )";

        command.CommandText += @"
            ORDER BY rank
            LIMIT @limit;
        ";

        command.Parameters.AddWithValue("@query", SearchUtils.ToFtsPhrase(query));
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<SymbolSearchResult>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                results.Add(new SymbolSearchResult(reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetString(3),
                    reader.GetString(2)));
        }

        // Architecture §10 "identifier fragments" contract: the FTS5 unicode61 index
        // matches whole tokens only (no camel-case split), so a fragment like
        // "Service" misses "CourseService". When FTS5 saturates fewer than the
        // requested limit, fall back to case-insensitive substring matching over
        // fully qualified names to fill the gap.
        if (results.Count < limit && SearchUtils.IsPlainIdentifierQuery(query)) return SearchSymbolsBySubstring(query, snapshotId, limit, includeGenerated, kind);

        return results;
    }

    /// <inheritdoc cref="ISearchStore.SearchSymbolsPage" />
    public SymbolSearchPage SearchSymbolsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return new SymbolSearchPage([], null);

        limit = Math.Max(1, limit);
        var fingerprint = SearchCursor.ComputeFingerprint(query, kind, includeGenerated);
        cursor?.Validate(snapshotId, fingerprint);

        var mode = cursor?.Mode ?? "fts";
        if (mode == "fts")
        {
            var page = SearchSymbolsFtsPage(query, snapshotId, limit, includeGenerated, kind, cursor);
            // Only fall back to substring on the *first* page of a plain identifier query.
            // Trigger when FTS5 saturates fewer than the requested limit.
            if (page.Items.Count < limit && cursor == null && SearchUtils.IsPlainIdentifierQuery(query))
                return SearchSymbolsBySubstringPage(query, snapshotId, limit, includeGenerated, kind, null);
            return page;
        }

        return SearchSymbolsBySubstringPage(query, snapshotId, limit, includeGenerated, kind, cursor);
    }

    private SymbolSearchPage SearchSymbolsFtsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_fts.symbol_id, fqn, doc_comment_id, kind, symbol_fts.rank AS match_rank
            FROM symbol_fts
            WHERE symbol_fts MATCH @query
              AND symbol_fts.snapshot_id = @snapshotId
            """;

        if (!string.IsNullOrEmpty(kind))
        {
            command.CommandText += " AND symbol_fts.kind = @kind";
            command.Parameters.AddWithValue("@kind", kind);
        }

        if (!includeGenerated)
            command.CommandText += @"
              AND (
                    NOT EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = symbol_fts.symbol_id
                    )
                 OR EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = symbol_fts.symbol_id
                          AND (d.is_generated = 0 OR d.is_generated IS NULL)
                    )
                  )";

        if (cursor != null)
        {
            command.CommandText += @"
              AND (symbol_fts.rank > @lastRank
                   OR (symbol_fts.rank = @lastRank AND symbol_fts.symbol_id > @lastSymbolId))";
            command.Parameters.AddWithValue("@lastRank", cursor.LastRank ?? double.NegativeInfinity);
            command.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
        }

        command.CommandText += @"
            ORDER BY rank, symbol_fts.symbol_id
            LIMIT @limit;
        ";

        command.Parameters.AddWithValue("@query", SearchUtils.ToFtsPhrase(query));
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit + 1);

        var results = new List<SymbolSearchResult>();
        var ranks = new List<double>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                results.Add(new SymbolSearchResult(reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetString(3),
                    reader.GetString(2)));
                ranks.Add(reader.GetDouble(4));
            }
        }

        return BuildPage(results, ranks, limit, snapshotId, query, kind, includeGenerated, "fts");
    }

    private SymbolSearchPage SearchSymbolsBySubstringPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId
              AND ss.fqn LIKE @pattern ESCAPE '\'
            """;

        if (!string.IsNullOrEmpty(kind))
        {
            command.CommandText += " AND s.kind = @kind";
            command.Parameters.AddWithValue("@kind", kind);
        }

        if (!includeGenerated)
            command.CommandText += @"
              AND (
                    NOT EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = s.symbol_id
                    )
                 OR EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = s.symbol_id
                          AND (d.is_generated = 0 OR d.is_generated IS NULL)
                    )
                  )";

        if (cursor != null)
        {
            command.CommandText += @"
              AND (ss.fqn > @lastFqn OR (ss.fqn = @lastFqn AND s.symbol_id > @lastSymbolId))";
            command.Parameters.AddWithValue("@lastFqn", cursor.LastFqn);
            command.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
        }

        command.CommandText += @"
            ORDER BY ss.fqn, s.symbol_id
            LIMIT @limit;
        ";

        var escaped = query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        command.Parameters.AddWithValue("@pattern", $"%{escaped}%");
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit + 1);

        var results = new List<SymbolSearchResult>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                results.Add(new SymbolSearchResult(reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.GetString(3),
                    reader.GetString(2)));
        }

        return BuildPage(results, null, limit, snapshotId, query, kind, includeGenerated, "substring");
    }

    private static SymbolSearchPage BuildPage(List<SymbolSearchResult> results, List<double>? ranks, int limit, string snapshotId, string query, string? kind, bool includeGenerated, string mode)
    {
        var hasMore = results.Count > limit;
        if (hasMore)
        {
            results.RemoveAt(results.Count - 1);
            ranks?.RemoveAt(ranks.Count - 1);
        }

        if (!hasMore || results.Count == 0)
            return new SymbolSearchPage(results, null);

        var last = results[^1];
        var nextCursor = new SearchCursor(
            snapshotId,
            SearchCursor.ComputeFingerprint(query, kind, includeGenerated),
            mode,
            ranks?[^1],
            last.FullyQualifiedName,
            last.SymbolId);

        return new SymbolSearchPage(results, nextCursor.Encode());
    }

    private List<SymbolSearchResult> SearchSymbolsBySubstring(string query, string snapshotId, int limit, bool includeGenerated, string? kind)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId
              AND ss.fqn LIKE @pattern ESCAPE '\'
            """;

        if (!string.IsNullOrEmpty(kind))
        {
            command.CommandText += " AND s.kind = @kind";
            command.Parameters.AddWithValue("@kind", kind);
        }

        // See the identical comment in SearchSymbols above: keep the
        // NOT EXISTS / EXISTS disjunction so declaration-less symbols remain searchable.
        if (!includeGenerated)
            command.CommandText += @"
              AND (
                    NOT EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = s.symbol_id
                    )
                 OR EXISTS (
                        SELECT 1 FROM declarations d
                        JOIN snapshot_documents sd
                          ON sd.snapshot_id = @snapshotId
                         AND sd.document_version_id = d.document_version_id
                        WHERE d.symbol_id = s.symbol_id
                          AND (d.is_generated = 0 OR d.is_generated IS NULL)
                    )
                  )";

        command.CommandText += @"
            ORDER BY ss.fqn
            LIMIT @limit;
        ";

        var escaped = query.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
        command.Parameters.AddWithValue("@pattern", $"%{escaped}%");
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<SymbolSearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new SymbolSearchResult(reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetString(3),
                reader.GetString(2)));
        return results;
    }

    /// <inheritdoc cref="ISearchStore.ResolveSymbolByFqn" />
    public IndexedSymbolInfo? ResolveSymbolByFqn(string fqn, string snapshotId, bool includeGenerated = false)
    {
        // Roslyn's FullyQualifiedFormat persists symbol FQNs with a "global::" prefix
        // (e.g. "global::Outcome.Validation.OrderValidator"), but callers naturally type
        // FQNs without it. Accept either form transparently.
        var globalFqn = fqn.StartsWith("global::", StringComparison.Ordinal) ? fqn : $"global::{fqn}";

        using var command = _connection.CreateCommand();

        command.CommandText = """
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                   (SELECT COUNT(*) FROM declarations d2
                      JOIN snapshot_documents sd2 ON sd2.snapshot_id = @snapshotId
                                                 AND sd2.document_version_id = d2.document_version_id
                    WHERE d2.symbol_id = s.symbol_id) AS decl_count,
                   (SELECT MAX(d3.is_partial) FROM declarations d3
                      JOIN snapshot_documents sd3 ON sd3.snapshot_id = @snapshotId
                                                 AND sd3.document_version_id = d3.document_version_id
                    WHERE d3.symbol_id = s.symbol_id) AS is_partial
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE (ss.fqn = @fqn OR ss.fqn = @globalFqn) AND ss.snapshot_id = @snapshotId
              AND EXISTS (SELECT 1 FROM declarations d
                            JOIN snapshot_documents sd ON sd.snapshot_id = @snapshotId
                                                      AND sd.document_version_id = d.document_version_id
                          WHERE d.symbol_id = s.symbol_id)
            """;

        if (!includeGenerated) command.CommandText += ExcludeGeneratedClause;

        command.CommandText += FqnOrderLimitClause;

        command.Parameters.AddWithValue("@fqn", fqn);
        command.Parameters.AddWithValue("@globalFqn", globalFqn);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
            return DeclarationReadStore.ReadSymbolInfo(reader);

        reader.Close();
        command.Parameters.Clear();
        command.CommandText = """
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                   (SELECT COUNT(*) FROM declarations d2
                      JOIN snapshot_documents sd2 ON sd2.snapshot_id = @snapshotId
                                                 AND sd2.document_version_id = d2.document_version_id
                    WHERE d2.symbol_id = s.symbol_id) AS decl_count,
                   (SELECT MAX(d3.is_partial) FROM declarations d3
                      JOIN snapshot_documents sd3 ON sd3.snapshot_id = @snapshotId
                                                 AND sd3.document_version_id = d3.document_version_id
                    WHERE d3.symbol_id = s.symbol_id) AS is_partial
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE (ss.fqn LIKE @pattern OR ss.fqn LIKE @globalPattern) AND ss.snapshot_id = @snapshotId
              AND EXISTS (SELECT 1 FROM declarations d
                            JOIN snapshot_documents sd ON sd.snapshot_id = @snapshotId
                                                      AND sd.document_version_id = d.document_version_id
                          WHERE d.symbol_id = s.symbol_id)
            """;

        if (!includeGenerated) command.CommandText += ExcludeGeneratedClause;

        command.CommandText += FqnOrderLimitClause;

        command.Parameters.AddWithValue("@pattern", $"{fqn}%");
        command.Parameters.AddWithValue("@globalPattern", $"{globalFqn}%");
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader2 = command.ExecuteReader();
        if (reader2.Read())
            return DeclarationReadStore.ReadSymbolInfo(reader2);

        return null;
    }

    /// <inheritdoc cref="ISearchStore.ResolveSymbolByDocCommentId" />
    public IndexedSymbolInfo? ResolveSymbolByDocCommentId(string docCommentId, string snapshotId, bool includeGenerated = false)
    {
        if (string.IsNullOrWhiteSpace(docCommentId))
            return null;

        using var command = _connection.CreateCommand();

        command.CommandText = """
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                   (SELECT COUNT(*) FROM declarations d2
                      JOIN snapshot_documents sd2 ON sd2.snapshot_id = @snapshotId
                                                 AND sd2.document_version_id = d2.document_version_id
                    WHERE d2.symbol_id = s.symbol_id) AS decl_count,
                   (SELECT MAX(d3.is_partial) FROM declarations d3
                      JOIN snapshot_documents sd3 ON sd3.snapshot_id = @snapshotId
                                                 AND sd3.document_version_id = d3.document_version_id
                    WHERE d3.symbol_id = s.symbol_id) AS is_partial
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE s.doc_comment_id = @docCommentId AND ss.snapshot_id = @snapshotId
            """;

        if (!includeGenerated) command.CommandText += ExcludeGeneratedClause;

        command.CommandText += FqnOrderLimitClause;

        command.Parameters.AddWithValue("@docCommentId", docCommentId);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (reader.Read())
            return DeclarationReadStore.ReadSymbolInfo(reader);

        return null;
    }
}