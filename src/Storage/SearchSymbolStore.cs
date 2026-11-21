// Purpose: symbol half of the search store.
// Owns: symbol FTS/substring search, keyset pagination, and FQN resolution.
// Must not contain: the other halves, or any Roslyn dependency.

using Microsoft.Data.Sqlite;

namespace Lurp.Storage;

internal sealed class SearchSymbolStore
{
    private static readonly string ExcludeGeneratedClause = $"""

              AND EXISTS (
                    SELECT 1 FROM declarations d
                    {SnapshotDeclarationLink.Join("d", "sdecl")}
                    WHERE d.symbol_id = s.symbol_id
                      AND (d.is_generated = 0 OR d.is_generated IS NULL)
                  )
        """;

    private static string ExcludeGeneratedOrExternalClause(string symbolIdColumn) => $"""

              AND (
                    NOT EXISTS (
                        SELECT 1 FROM declarations d
                        {SnapshotDeclarationLink.Join("d", "sdecl")}
                        WHERE d.symbol_id = {symbolIdColumn}
                    )
                 OR EXISTS (
                        SELECT 1 FROM declarations d
                        {SnapshotDeclarationLink.Join("d", "sdecl")}
                        WHERE d.symbol_id = {symbolIdColumn}
                          AND (d.is_generated = 0 OR d.is_generated IS NULL)
                    )
                  )
        """;

    private readonly SqliteConnection _connection;

    public SearchSymbolStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    ///     Keyset-paginated symbol search in one sequence (D73): tier 1 whole-token matches
    ///     over FQN (by rank, FQN, symbol id), then tier 2 case-insensitive substring matches
    ///     over FQN (by FQN, symbol id) for plain identifier queries.
    /// </summary>
    public SymbolSearchPage SearchSymbolsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return new SymbolSearchPage([], null);

        var fingerprint = SearchCursor.ComputeFingerprint(query, kind, includeGenerated);
        cursor?.Validate(snapshotId, fingerprint);

        var ftsQuery = SearchUtils.ToColumnFtsPhrase("fqn", query);
        var isPlainIdentifier = SearchUtils.IsPlainIdentifierQuery(query);

        if (cursor is { Mode: "substring" })
        {
            if (!isPlainIdentifier)
                return new SymbolSearchPage([], null);

            var tier2Only = ReadSubstringTier(query, ftsQuery, snapshotId, limit + 1, includeGenerated, kind, cursor);
            var hasMoreTier2 = tier2Only.Count > limit;
            if (hasMoreTier2)
                tier2Only.RemoveAt(limit);

            var tier2Cursor = hasMoreTier2
                ? BuildCursor(snapshotId, fingerprint, "substring", null, tier2Only[^1])
                : null;
            return new SymbolSearchPage(tier2Only, tier2Cursor);
        }

        var tier1 = ReadFtsTier(ftsQuery, snapshotId, limit + 1, includeGenerated, kind, cursor);
        if (tier1.Count > limit)
        {
            tier1.RemoveAt(limit);
            var tier1Cursor = BuildCursor(snapshotId, fingerprint, "fts", tier1[^1].Rank, tier1[^1].Result);
            return new SymbolSearchPage(tier1.ConvertAll(static r => r.Result), tier1Cursor);
        }

        var results = tier1.ConvertAll(static r => r.Result);
        if (!isPlainIdentifier)
            return new SymbolSearchPage(results, null);

        var remaining = limit - results.Count;
        var tier2 = ReadSubstringTier(query, ftsQuery, snapshotId, remaining + 1, includeGenerated, kind, cursor: null);
        var hasMore = tier2.Count > remaining;
        var takeCount = Math.Min(remaining, tier2.Count);
        for (var i = 0; i < takeCount; i++)
            results.Add(tier2[i]);

        if (!hasMore)
            return new SymbolSearchPage(results, null);

        var nextCursor = takeCount > 0
            ? BuildCursor(snapshotId, fingerprint, "substring", null, results[^1])
            : BuildCursor(snapshotId, fingerprint, "fts", tier1[^1].Rank, tier1[^1].Result);
        return new SymbolSearchPage(results, nextCursor);
    }

    private List<(SymbolSearchResult Result, double Rank)> ReadFtsTier(
        string ftsQuery,
        string snapshotId,
        int limit,
        bool includeGenerated,
        string? kind,
        SearchCursor? cursor)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_fts.symbol_id, symbol_fts.fqn, symbol_fts.doc_comment_id, symbol_fts.kind, symbol_fts.rank AS match_rank
            FROM symbol_fts
            WHERE symbol_fts MATCH @ftsQuery
              AND symbol_fts.snapshot_id = @snapshotId
            """;

        AppendKindFilter(command, "symbol_fts.kind", kind);
        if (!includeGenerated)
            command.CommandText += ExcludeGeneratedOrExternalClause("symbol_fts.symbol_id");

        if (cursor != null)
        {
            command.CommandText += """

                  AND (symbol_fts.rank > @lastRank
                       OR (symbol_fts.rank = @lastRank AND symbol_fts.fqn > @lastFqn)
                       OR (symbol_fts.rank = @lastRank AND symbol_fts.fqn = @lastFqn AND symbol_fts.symbol_id > @lastSymbolId))
                """;
            command.Parameters.AddWithValue("@lastRank", cursor.LastRank ?? double.NegativeInfinity);
            command.Parameters.AddWithValue("@lastFqn", cursor.LastFqn);
            command.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
        }

        command.CommandText += """

            ORDER BY symbol_fts.rank, symbol_fts.fqn, symbol_fts.symbol_id
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@ftsQuery", ftsQuery);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit);

        var rows = new List<(SymbolSearchResult Result, double Rank)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var result = new SymbolSearchResult(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetString(3),
                reader.GetString(2));
            rows.Add((result, reader.GetDouble(4)));
        }

        return rows;
    }

    private List<SymbolSearchResult> ReadSubstringTier(
        string query,
        string ftsQuery,
        string snapshotId,
        int limit,
        bool includeGenerated,
        string? kind,
        SearchCursor? cursor)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.symbol_id, ss.fqn, s.doc_comment_id, s.kind
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId
              AND {TextCaseMatching.SqlFunctionName}(ss.fqn, @query)
              AND ss.symbol_id NOT IN (
                  SELECT symbol_id
                  FROM symbol_fts
                  WHERE symbol_fts MATCH @ftsQuery
                    AND snapshot_id = @snapshotId
              )
            """;

        AppendKindFilter(command, "s.kind", kind);
        if (!includeGenerated)
            command.CommandText += ExcludeGeneratedOrExternalClause("s.symbol_id");

        if (cursor != null)
        {
            command.CommandText += """

                  AND (ss.fqn > @lastFqn OR (ss.fqn = @lastFqn AND s.symbol_id > @lastSymbolId))
                """;
            command.Parameters.AddWithValue("@lastFqn", cursor.LastFqn);
            command.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
        }

        command.CommandText += """

            ORDER BY ss.fqn, s.symbol_id
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@ftsQuery", ftsQuery);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@limit", limit);

        var rows = new List<SymbolSearchResult>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new SymbolSearchResult(
                reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.GetString(3),
                reader.GetString(2)));
        }

        return rows;
    }

    private static void AppendKindFilter(SqliteCommand command, string columnName, string? kind)
    {
        if (!string.IsNullOrEmpty(kind))
        {
            command.CommandText += $" AND {columnName} = @kind";
            command.Parameters.AddWithValue("@kind", kind);
        }
    }

    private static string BuildCursor(string snapshotId, string fingerprint, string mode, double? lastRank, SymbolSearchResult last)
    {
        return new SearchCursor(
            snapshotId,
            fingerprint,
            mode,
            lastRank,
            last.FullyQualifiedName,
            last.SymbolId).Encode();
    }

    /// <summary>Finds every linked symbol matching one exact key in <paramref name="snapshotId" />, ordered by <c>symbol_id</c>.</summary>
    public List<IndexedSymbolInfo> FindSymbols(SymbolKey key, string value, string snapshotId, bool includeGenerated = false)
    {
        if (string.IsNullOrEmpty(value))
            return [];

        var column = key switch
        {
            SymbolKey.SymbolId => "s.symbol_id",
            SymbolKey.DocCommentId => "s.doc_comment_id",
            SymbolKey.Fqn => "ss.fqn",
            SymbolKey.BareFqn => "ss.bare_fqn",
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null)
        };

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
            {DeclarationReadStore.SymbolDeclarationAggregateColumns()}
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE ss.snapshot_id = @snapshotId
              AND {column} = @value
              AND EXISTS (SELECT 1 FROM declarations d
                            {SnapshotDeclarationLink.Join("d", "sdecl")}
                          WHERE d.symbol_id = s.symbol_id)
            """;

        if (!includeGenerated)
            command.CommandText += ExcludeGeneratedClause;

        command.CommandText += " ORDER BY s.symbol_id;";

        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<IndexedSymbolInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(DeclarationReadStore.ReadSymbolInfo(reader));
        return results;
    }
}