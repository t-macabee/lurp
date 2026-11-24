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
        var hasKind = !string.IsNullOrEmpty(kind);
        var nameQuery = $"name_tokens : ({literal} OR {prefix})";
        var containerQuery = $"container_tokens : {prefix} OR fqn : {literal}";
        var exactQuery = $"simple_name : {literal}";

        // A non-identifier query (for example "XML Par") has no substring tiers: its whole
        // result set is the union of the FTS matches, reached through the MATCH index.
        if (!SearchUtils.IsPlainIdentifierQuery(term))
        {
            using var command = _connection.CreateCommand();
            command.CommandText = NonIdentifierSql(includeGenerated, hasKind, cursor != null);
            BindCommonParameters(command, snapshotId, term, limit + 1);
            command.Parameters.AddWithValue("@nameQuery", nameQuery);
            command.Parameters.AddWithValue("@containerQuery", containerQuery);
            command.Parameters.AddWithValue("@exactQuery", exactQuery);
            if (hasKind)
                command.Parameters.AddWithValue("@kind", kind!);
            BindCursor(command, cursor);
            return BuildPage(ReadRows(command), limit, snapshotId, fingerprint);
        }

        var rows = new List<RankedSymbolRow>();
        if (cursor == null || cursor.Tier <= 2)
        {
            // Tiers 1 and 2 are the exact and token/prefix name matches. The exact match is a
            // simple_name MATCH of its own because a camel-case name such as AlphaBeta keeps
            // "Alpha Beta" in name_tokens, so the query "alphabeta" does not reach it there.
            using (var command = _connection.CreateCommand())
            {
                command.CommandText = Tier12Sql(includeGenerated, hasKind, cursor != null);
                BindCommonParameters(command, snapshotId, term, limit + 1);
                command.Parameters.AddWithValue("@nameQuery", nameQuery);
                command.Parameters.AddWithValue("@exactQuery", exactQuery);
                if (hasKind)
                    command.Parameters.AddWithValue("@kind", kind!);
                BindCursor(command, cursor);
                rows.AddRange(ReadRows(command));
            }

            if (rows.Count > limit)
                return BuildPage(rows, limit, snapshotId, fingerprint);

            // Only a short tiers 1-2 read reaches the substring tiers, which cannot use the FTS
            // index. Name and exact matches are excluded so no row appears in two tiers.
            using var tail = _connection.CreateCommand();
            tail.CommandText = Tier34Sql(includeGenerated, hasKind, hasCursor: false);
            BindCommonParameters(tail, snapshotId, term, limit - rows.Count + 1);
            tail.Parameters.AddWithValue("@nameQuery", nameQuery);
            tail.Parameters.AddWithValue("@containerQuery", containerQuery);
            tail.Parameters.AddWithValue("@exactQuery", exactQuery);
            if (hasKind)
                tail.Parameters.AddWithValue("@kind", kind!);
            rows.AddRange(ReadRows(tail));
            return BuildPage(rows, limit, snapshotId, fingerprint);
        }

        using var cursorCommand = _connection.CreateCommand();
        cursorCommand.CommandText = Tier34Sql(includeGenerated, hasKind, hasCursor: true);
        BindCommonParameters(cursorCommand, snapshotId, term, limit + 1);
        cursorCommand.Parameters.AddWithValue("@nameQuery", nameQuery);
        cursorCommand.Parameters.AddWithValue("@containerQuery", containerQuery);
        cursorCommand.Parameters.AddWithValue("@exactQuery", exactQuery);
        if (hasKind)
            cursorCommand.Parameters.AddWithValue("@kind", kind!);
        BindCursor(cursorCommand, cursor);
        return BuildPage(ReadRows(cursorCommand), limit, snapshotId, fingerprint);
    }

    /// <summary>
    ///     The non-identifier query: name, container and exact simple-name matches are joined
    ///     by rowid, so <c>symbol_fts</c> is read through its MATCH index and a rowid lookup
    ///     only. One owner: the plan test pins this text.
    /// </summary>
    internal static string NonIdentifierSql(bool includeGenerated, bool hasKind, bool hasCursor) => $"""
        WITH name_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @nameQuery AND snapshot_id = @snapshotId
        ),
        container_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @containerQuery AND snapshot_id = @snapshotId
        ),
        exact_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @exactQuery AND snapshot_id = @snapshotId
        ),
        candidates AS (
            SELECT rowid FROM name_matches
            UNION
            SELECT rowid FROM container_matches
            UNION
            SELECT rowid FROM exact_matches
        ),
        ranked AS (
            SELECT f.symbol_id, f.fqn, f.doc_comment_id, f.kind, f.is_test, f.kind_rank,
                   CASE
                       WHEN e.rowid IS NOT NULL AND lurp_symbol_name_equals(f.simple_name, @term) THEN 1
                       WHEN n.rowid IS NOT NULL THEN 2
                       WHEN c.rowid IS NOT NULL THEN 4
                   END AS tier,
                   CASE
                       WHEN n.rowid IS NOT NULL THEN length(f.name_tokens)
                       WHEN c.rowid IS NOT NULL THEN 0
                       ELSE 1
                   END AS token_rank
            FROM candidates
            JOIN symbol_fts AS f ON f.rowid = candidates.rowid
            LEFT JOIN name_matches AS n ON n.rowid = f.rowid
            LEFT JOIN container_matches AS c ON c.rowid = f.rowid
            LEFT JOIN exact_matches AS e ON e.rowid = f.rowid
            WHERE 1 = 1
            {(hasKind ? "AND f.kind = @kind" : "")}
            {(includeGenerated ? "" : ExcludeGeneratedOrExternalClause("f.symbol_id"))}
        )
        SELECT symbol_id, fqn, doc_comment_id, kind, tier, is_test, kind_rank,
               CASE WHEN tier IN (2, 4) THEN token_rank ELSE 0 END AS match_rank
        FROM ranked
        WHERE tier IS NOT NULL{CursorPredicate(hasCursor)}
        ORDER BY tier, is_test, kind_rank, match_rank, fqn, symbol_id
        LIMIT @limitPlusOne;
        """;

    /// <summary>
    ///     Tiers 1 and 2 of a plain-identifier query: the exact and token/prefix name matches.
    ///     The exact tier comes from its own <c>simple_name</c> MATCH because a camel-case name
    ///     such as <c>AlphaBeta</c> keeps "Alpha Beta" in <c>name_tokens</c>, which the query
    ///     "alphabeta" cannot reach. One owner: the plan test pins this text.
    /// </summary>
    internal static string Tier12Sql(bool includeGenerated, bool hasKind, bool hasCursor) => $"""
        WITH name_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @nameQuery AND snapshot_id = @snapshotId
        ),
        exact_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @exactQuery AND snapshot_id = @snapshotId
        ),
        candidates AS (
            SELECT rowid FROM name_matches
            UNION
            SELECT rowid FROM exact_matches
        ),
        ranked AS (
            SELECT f.symbol_id, f.fqn, f.doc_comment_id, f.kind, f.is_test, f.kind_rank,
                   CASE WHEN lurp_symbol_name_equals(f.simple_name, @term) THEN 1 ELSE 2 END AS tier,
                   length(f.name_tokens) AS token_rank
            FROM candidates AS n
            JOIN symbol_fts AS f ON f.rowid = n.rowid
            WHERE 1 = 1
            {(hasKind ? "AND f.kind = @kind" : "")}
            {(includeGenerated ? "" : ExcludeGeneratedOrExternalClause("f.symbol_id"))}
        )
        SELECT symbol_id, fqn, doc_comment_id, kind, tier, is_test, kind_rank,
               CASE WHEN tier = 2 THEN token_rank ELSE 0 END AS match_rank
        FROM ranked
        WHERE 1 = 1{CursorPredicate(hasCursor)}
        ORDER BY tier, is_test, kind_rank, match_rank, fqn, symbol_id
        LIMIT @limitPlusOne;
        """;

    /// <summary>
    ///     Tiers 3 and 4 of a plain-identifier query: the substring and containing-name matches,
    ///     with every name and exact match excluded so no row appears in two tiers. Tier 3 orders
    ///     by name alone; only tier 4 uses the container-match rank. One owner: the plan test
    ///     pins this text.
    /// </summary>
    internal static string Tier34Sql(bool includeGenerated, bool hasKind, bool hasCursor) => $"""
        WITH name_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @nameQuery AND snapshot_id = @snapshotId
        ),
        exact_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @exactQuery AND snapshot_id = @snapshotId
        ),
        container_matches AS MATERIALIZED (
            SELECT rowid FROM symbol_fts
            WHERE symbol_fts MATCH @containerQuery AND snapshot_id = @snapshotId
        ),
        ranked AS (
            SELECT f.symbol_id, f.fqn, f.doc_comment_id, f.kind, f.is_test, f.kind_rank,
                   CASE WHEN {TextCaseMatching.SqlFunctionName}(f.simple_name, @term) THEN 3 ELSE 4 END AS tier,
                   CASE WHEN c.rowid IS NOT NULL THEN 0 ELSE 1 END AS token_rank
            FROM symbol_fts AS f
            LEFT JOIN container_matches AS c ON c.rowid = f.rowid
            WHERE f.snapshot_id = @snapshotId
              AND f.rowid NOT IN (SELECT rowid FROM name_matches)
              AND f.rowid NOT IN (SELECT rowid FROM exact_matches)
              AND ({TextCaseMatching.SqlFunctionName}(f.simple_name, @term)
                   OR c.rowid IS NOT NULL
                   OR {TextCaseMatching.SqlFunctionName}(f.fqn, @term))
            {(hasKind ? "AND f.kind = @kind" : "")}
            {(includeGenerated ? "" : ExcludeGeneratedOrExternalClause("f.symbol_id"))}
        )
        SELECT symbol_id, fqn, doc_comment_id, kind, tier, is_test, kind_rank,
               CASE WHEN tier = 4 THEN token_rank ELSE 0 END AS match_rank
        FROM ranked
        WHERE 1 = 1{CursorPredicate(hasCursor)}
        ORDER BY tier, is_test, kind_rank, match_rank, fqn, symbol_id
        LIMIT @limitPlusOne;
        """;

    private static string CursorPredicate(bool hasCursor) => hasCursor
        ? """

              AND (tier, is_test, kind_rank, match_rank, fqn, symbol_id) >
                  (@tier, @isTest, @kindRank, @lastRank, @lastFqn, @lastSymbolId)
            """
        : "";

    private static void BindCommonParameters(SqliteCommand command, string snapshotId, string term, int fetchLimit)
    {
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@term", term);
        command.Parameters.AddWithValue("@limitPlusOne", (long)fetchLimit);
    }

    private static void BindCursor(SqliteCommand command, SearchCursor? cursor)
    {
        if (cursor == null)
            return;

        command.Parameters.AddWithValue("@tier", cursor.Tier);
        command.Parameters.AddWithValue("@isTest", cursor.IsTest);
        command.Parameters.AddWithValue("@kindRank", cursor.KindRank);
        command.Parameters.AddWithValue("@lastRank", cursor.LastRank);
        command.Parameters.AddWithValue("@lastFqn", cursor.LastFqn);
        command.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
    }

    private static List<RankedSymbolRow> ReadRows(SqliteCommand command)
    {
        var rows = new List<RankedSymbolRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RankedSymbolRow(
                new SymbolSearchResult(reader.GetString(0), reader.GetString(1), reader.GetString(3), reader.GetString(2)),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)));
        }

        return rows;
    }

    private static SymbolSearchPage BuildPage(List<RankedSymbolRow> rows, int limit, string snapshotId, string fingerprint)
    {
        var page = rows.Take(limit).Select(static row => row.Result).ToList();
        if (rows.Count <= limit)
            return new SymbolSearchPage(page, null);

        var last = rows[limit - 1];
        var next = new SearchCursor(snapshotId, fingerprint, last.Tier, last.IsTest, last.KindRank,
            last.MatchRank, last.Result.FullyQualifiedName, last.Result.SymbolId);
        return new SymbolSearchPage(page, next.Encode());
    }

    private sealed record RankedSymbolRow(SymbolSearchResult Result, int Tier, int IsTest, int KindRank, int MatchRank);

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