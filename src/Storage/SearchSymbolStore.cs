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
        _connection.CreateFunction<string, string, bool>("lurp_symbol_name_equals",
            static (name, query) => string.Equals(name, query, StringComparison.OrdinalIgnoreCase),
            isDeterministic: true);
    }

    /// <summary>Ranks each match once and pages by the same key across all four tiers.</summary>
    public SymbolSearchPage SearchSymbolsPage(string query, string snapshotId, int limit, bool includeGenerated, string? kind, SearchCursor? cursor)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return new SymbolSearchPage([], null);

        var fingerprint = SearchCursor.ComputeFingerprint(query, kind, includeGenerated);
        cursor?.Validate(snapshotId, fingerprint);
        var term = query.Trim();
        var literal = SearchUtils.ToFtsPhrase(term);
        var prefix = SearchUtils.ToFtsPhrase(SymbolSearchNames.Tokenize(term)) + "*";

        // Rank from the row's text so indexing another snapshot cannot change this cursor's key.
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            WITH name_matches AS MATERIALIZED (
                SELECT rowid FROM symbol_fts
                WHERE symbol_fts MATCH @nameQuery AND snapshot_id = @snapshotId
            ),
            container_matches AS MATERIALIZED (
                SELECT rowid FROM symbol_fts
                WHERE symbol_fts MATCH @containerQuery AND snapshot_id = @snapshotId
            ),
            ranked AS (
                SELECT f.symbol_id, f.fqn, f.doc_comment_id, f.kind, f.is_test, f.kind_rank,
                       CASE
                           WHEN lurp_symbol_name_equals(f.simple_name, @term) THEN 1
                           WHEN n.rowid IS NOT NULL THEN 2
                           WHEN @plainIdentifier AND {TextCaseMatching.SqlFunctionName}(f.simple_name, @term) THEN 3
                           WHEN c.rowid IS NOT NULL OR
                                (@plainIdentifier AND {TextCaseMatching.SqlFunctionName}(f.fqn, @term)) THEN 4
                       END AS tier,
                       CASE
                           WHEN n.rowid IS NOT NULL THEN length(f.name_tokens)
                           WHEN c.rowid IS NOT NULL THEN 0
                           ELSE 1
                       END AS token_rank
                FROM symbol_fts f
                LEFT JOIN name_matches n ON n.rowid = f.rowid
                LEFT JOIN container_matches c ON c.rowid = f.rowid
                WHERE f.snapshot_id = @snapshotId
                {(string.IsNullOrEmpty(kind) ? "" : "AND f.kind = @kind")}
                {(includeGenerated ? "" : ExcludeGeneratedOrExternalClause("f.symbol_id"))}
            )
            SELECT symbol_id, fqn, doc_comment_id, kind, tier, is_test, kind_rank,
                   CASE WHEN tier IN (2, 4) THEN token_rank ELSE 0 END AS match_rank
            FROM ranked AS r
            WHERE tier IS NOT NULL
            """;

        if (cursor != null)
        {
            command.CommandText += """

                  AND (tier, is_test, kind_rank, match_rank, fqn, symbol_id) >
                      (@tier, @isTest, @kindRank, @lastRank, @lastFqn, @lastSymbolId)
                """;
            command.Parameters.AddWithValue("@tier", cursor.Tier);
            command.Parameters.AddWithValue("@isTest", cursor.IsTest);
            command.Parameters.AddWithValue("@kindRank", cursor.KindRank);
            command.Parameters.AddWithValue("@lastRank", cursor.LastRank);
            command.Parameters.AddWithValue("@lastFqn", cursor.LastFqn);
            command.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
        }

        command.CommandText += """

            ORDER BY tier, is_test, kind_rank, match_rank, fqn, symbol_id
            LIMIT @limitPlusOne;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@term", term);
        command.Parameters.AddWithValue("@plainIdentifier", SearchUtils.IsPlainIdentifierQuery(term));
        command.Parameters.AddWithValue("@nameQuery", $"name_tokens : ({literal} OR {prefix})");
        command.Parameters.AddWithValue("@containerQuery", $"container_tokens : {prefix} OR fqn : {literal}");
        command.Parameters.AddWithValue("@limitPlusOne", (long)limit + 1);
        if (!string.IsNullOrEmpty(kind))
            command.Parameters.AddWithValue("@kind", kind);

        var results = new List<SymbolSearchResult>();
        SearchCursor? last = null;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (results.Count == limit)
                return new SymbolSearchPage(results, last!.Encode());

            var result = new SymbolSearchResult(reader.GetString(0), reader.GetString(1),
                reader.GetString(3), reader.GetString(2));
            results.Add(result);
            last = new SearchCursor(snapshotId, fingerprint, reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetInt32(7), result.FullyQualifiedName, result.SymbolId);
        }

        return new SymbolSearchPage(results, null);
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