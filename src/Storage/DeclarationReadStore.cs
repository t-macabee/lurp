using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;

namespace Lurp.Storage;

internal sealed class DeclarationReadStore
{
    private static readonly byte[] DeclaredNamePlaceholder = "<DECLARED_NAME>"u8.ToArray();
    private const string AssemblySimpleNameCollation = "lurp_assembly_simple_name";
    private readonly SqliteConnection _connection;

    internal DeclarationReadStore(SqliteConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.CreateCollation(AssemblySimpleNameCollation, static (left, right) =>
            ProjectFacts.AssemblyNameComparer.Compare(
                left == null ? null : MetadataReferenceIdentity.SimpleName(left),
                right == null ? null : MetadataReferenceIdentity.SimpleName(right)));
    }

    internal static string SymbolDeclarationAggregateColumns() => $"""
                   (SELECT COUNT(*) FROM declarations d2
                      {SnapshotDeclarationLink.Join("d2", "sdecl2")}
                    WHERE d2.symbol_id = s.symbol_id) AS decl_count,
                   (SELECT MAX(d3.is_partial) FROM declarations d3
                      {SnapshotDeclarationLink.Join("d3", "sdecl3")}
                    WHERE d3.symbol_id = s.symbol_id) AS is_partial
        """;

    internal IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
            {SymbolDeclarationAggregateColumns()}
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE s.symbol_id = @symbolId AND ss.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadSymbolInfo(reader);
    }

    internal List<(string SymbolId, string AccessorKind)> GetAccessorsForSymbol(string snapshotId, string associatedSymbolId)
    {
        var typeId = SymbolId.DeriveContainingTypeSymbolId(associatedSymbolId);
        if (typeId == null)
            return [];

        // Scope the metadata lookup to the declaring type's members.
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT e.target_symbol_id, json_extract(ss.metadata_json, '$.{SymbolMetadataKeys.AccessorKind}')
            FROM edges e
            JOIN snapshot_symbols ss ON ss.snapshot_id = e.snapshot_id AND ss.symbol_id = e.target_symbol_id
            WHERE e.snapshot_id = @snapshotId AND e.source_symbol_id = @typeId AND e.kind = 'Declares'
              AND json_extract(ss.metadata_json, '$.{SymbolMetadataKeys.AssociatedSymbol}') = @associatedDocId
            ORDER BY e.target_symbol_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@typeId", typeId);
        command.Parameters.AddWithValue("@associatedDocId", SymbolId.DocCommentIdOf(associatedSymbolId));

        using var reader = command.ExecuteReader();
        var accessors = new List<(string SymbolId, string AccessorKind)>();
        while (reader.Read())
            if (!reader.IsDBNull(1))
                accessors.Add((reader.GetString(0), reader.GetString(1)));

        return accessors;
    }

    internal string? GetSymbolSource(string symbolId, string snapshotId, ViewKind viewKind, bool includeGenerated = false)
    {
        string startCol, endCol;
        switch (viewKind)
        {
            case ViewKind.Declaration:
                startCol = "full_start";
                endCol = "full_end";
                break;
            case ViewKind.Signature:
                startCol = "signature_start";
                endCol = "signature_end";
                break;
            case ViewKind.Body:
                startCol = "body_start";
                endCol = "body_end";
                break;
            // default: ContainingType/Surrounding intentionally routed to GetContainingTypeSource/GetSurroundingLines — throw is intentional
            default:
                throw new ArgumentOutOfRangeException(nameof(viewKind), viewKind,
                    "Use GetContainingTypeSource or GetSurroundingLines for this view kind.");
        }

        var views = GetSymbolSpanContents(symbolId, snapshotId, startCol, endCol, includeGenerated)
            .Where(static span => span is { Content: not null, Start: not null, End: not null })
            .Select(span => SourceLineMap.SliceUtf8(span.Content!, span.Start!.Value, span.End!.Value, span.DocumentVersionId))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return views.Count == 0 ? null : string.Join("\n\n", views);
    }

    internal string? GetContainingTypeSource(string symbolId, string snapshotId, bool includeGenerated)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.doc_comment_id, s.assembly_identity
            FROM symbols s
            JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id
            WHERE s.symbol_id = @symbolId AND ss.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        var docCommentId = reader.GetString(0);
        var assemblyIdentity = reader.GetString(1);

        var parentDocCommentId = DeriveParentTypeDocCommentId(docCommentId);
        if (parentDocCommentId == null)
            return null;

        var parentSymbolId = SymbolId.Compose(parentDocCommentId, assemblyIdentity);

        return GetSymbolSource(parentSymbolId, snapshotId, ViewKind.Declaration, includeGenerated);
    }

    internal string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines, bool includeGenerated)
    {
        var views = new List<string>();
        foreach (var span in GetSymbolSpanContents(symbolId, snapshotId, "full_start", "full_end", includeGenerated))
        {
            if (span.Content == null || span.Start == null || span.End == null || span.LineStarts is not { Length: > 0 })
                continue;

            // These are 0-based indexes into line_starts (never consumer-facing
            // line numbers); names carry the Index suffix to keep the boundary
            // with the 1-based DeclarationLocation values self-documenting.
            var startLineIndex = SourceLineMap.FindLineIndex(span.LineStarts, span.Start.Value);
            var endLineIndex = SourceLineMap.FindEndLineIndex(span.LineStarts, span.Start.Value, span.End.Value);
            var (byteStart, byteEnd, _, _) = SourceLineMap.ExpandLineWindow(
                span.LineStarts, startLineIndex, endLineIndex, contextLines, span.Content.Length);
            var source = SourceLineMap.SliceUtf8(span.Content, byteStart, byteEnd, span.DocumentVersionId);
            if (!views.Contains(source, StringComparer.Ordinal))
                views.Add(source);
        }

        return views.Count == 0 ? null : string.Join("\n\n", views);
    }

    internal List<DeclarationLocation> GetDeclarationLocations(string symbolId, string snapshotId, bool includeGenerated = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT doc.relative_path, d.full_start, d.full_end, dv.line_starts, dv.content,
                   COALESCE(d.is_generated, 0), d.document_version_id
            FROM declarations d
            {SnapshotDeclarationLink.Join("d", "sdecl")}
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE d.symbol_id = @symbolId
            """;
        if (!includeGenerated)
            command.CommandText += " AND COALESCE(d.is_generated, 0) = 0";
        command.CommandText += " ORDER BY doc.relative_path, d.full_start, d.full_end;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        var results = new List<DeclarationLocation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var documentVersionId = reader.GetString(6);
            var location = SourceLineMap.MapDeclaration(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                SourceLineMap.ParseLineStarts(reader.IsDBNull(3) ? null : reader.GetString(3), documentVersionId),
                reader.IsDBNull(4) ? null : (byte[])reader[4],
                reader.GetInt32(5) == 1,
                documentVersionId,
                symbolId);
            if (location != null)
                results.Add(location);
        }

        return results;
    }

    internal Dictionary<string, List<string>> GetDocumentVersionIdsBySymbol(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.symbol_id, d.document_version_id
            FROM declarations d
            {SnapshotDeclarationLink.Join("d", "sdecl")}
            ORDER BY d.symbol_id, d.document_version_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        return ReadDocumentVersionIdsBySymbol(command);
    }

    // The scoped form of the full map above: the same rows restricted to the listed symbols.
    internal static string DocumentVersionIdsBySymbolsSql() => $"""
        SELECT d.symbol_id, d.document_version_id
        FROM json_each(@symbolIds) j
        CROSS JOIN declarations d ON d.symbol_id = j.value
        {SnapshotDeclarationLink.Join("d", "sdecl")}
        ORDER BY d.symbol_id, d.document_version_id;
        """;

    internal Dictionary<string, List<string>> GetDocumentVersionIdsBySymbol(string snapshotId, IEnumerable<string> symbolIds)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = DocumentVersionIdsBySymbolsSql();
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@symbolIds", symbolIds);

        return ReadDocumentVersionIdsBySymbol(command);
    }

    private static Dictionary<string, List<string>> ReadDocumentVersionIdsBySymbol(SqliteCommand command)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var symbolId = reader.GetString(0);
            if (!result.TryGetValue(symbolId, out var documentVersionIds))
            {
                documentVersionIds = [];
                result[symbolId] = documentVersionIds;
            }

            var documentVersionId = reader.GetString(1);
            if (documentVersionIds.Count == 0 || documentVersionIds[^1] != documentVersionId)
                documentVersionIds.Add(documentVersionId);
        }

        return result;
    }

    internal Dictionary<string, SymbolDiffInfo> GetSymbolDiffInfoBySymbol(string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.doc_comment_id, ss.fqn, ss.metadata_json
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new Dictionary<string, SymbolDiffInfo>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0), new SymbolDiffInfo(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    private List<SymbolSpanContent> GetSymbolSpanContents(string symbolId, string snapshotId, string startCol, string endCol, bool includeGenerated = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT dv.content, d.{startCol}, d.{endCol}, dv.line_starts, dv.document_version_id
            FROM snapshot_symbols ss
            JOIN declarations d ON d.symbol_id = ss.symbol_id
            {SnapshotDeclarationLink.Join("d", "sdecl")}
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE ss.snapshot_id = @snapshotId
              AND ss.symbol_id = @symbolId
            """;

        if (!includeGenerated) command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";

        command.CommandText += " ORDER BY doc.relative_path, d.full_start, d.full_end;";

        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<SymbolSpanContent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lineStarts = SourceLineMap.ParseLineStarts(
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
            results.Add(new SymbolSpanContent(
                reader.IsDBNull(0) ? null : (byte[])reader[0],
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                lineStarts,
                reader.GetString(4)));
        }

        return results;
    }

    internal static IndexedSymbolInfo ReadSymbolInfo(SqliteDataReader reader)
    {
        var sid = new SymbolId(reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(4) ? null : reader.GetString(4));

        var kindStr = reader.GetString(3);
        if (!Enum.TryParse<IndexedSymbolKind>(kindStr, true, out var kind))
            kind = IndexedSymbolKind.Unknown;

        return new IndexedSymbolInfo(sid, kind, reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6),
            !reader.IsDBNull(7) && reader.GetInt32(7) == 1);
    }

    internal IReadOnlyList<SymbolTransitionCandidate> LoadTransitionCandidates(
        string snapshotId,
        IReadOnlyCollection<string> symbolIds)
    {
        if (symbolIds.Count == 0)
            return [];

        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.symbol_id, s.kind, s.assembly_identity, ss.fqn,
                   d.signature_start, d.signature_end,
                   d.name_start, d.name_end,
                   d.body_start, d.body_end,
                   doc.relative_path, dv.content
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            JOIN declarations d ON d.symbol_id = ss.symbol_id
            {SnapshotDeclarationLink.Join("d", "sdecl", "ss.snapshot_id")}
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE ss.snapshot_id = @snapshotId
              AND s.symbol_id IN (SELECT value FROM json_each(@symbolIds))
              AND (d.is_generated = 0 OR d.is_generated IS NULL)
            ORDER BY s.symbol_id, doc.relative_path, d.signature_start;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        SqlIdList.Bind(command, "@symbolIds", symbolIds);

        var declarationsBySymbol = new Dictionary<string, List<DeclarationFingerprint>>(StringComparer.Ordinal);
        var symbolMeta = new Dictionary<string, (IndexedSymbolKind Kind, string AssemblyIdentity, string? Fqn)>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var symbolId = reader.GetString(0);
            var kindStr = reader.GetString(1);
            var assemblyIdentity = reader.GetString(2);
            var fqn = reader.IsDBNull(3) ? null : reader.GetString(3);
            var sigStart = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
            var sigEnd = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
            var nameStart = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            var nameEnd = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
            var bodyStart = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
            var bodyEnd = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9);
            var documentPath = reader.GetString(10);
            var content = reader.IsDBNull(11) ? null : (byte[])reader[11];

            if (!symbolMeta.ContainsKey(symbolId))
            {
                if (!Enum.TryParse<IndexedSymbolKind>(kindStr, true, out var kind))
                    kind = IndexedSymbolKind.Unknown;
                symbolMeta[symbolId] = (kind, assemblyIdentity, fqn);
            }

            if (content == null || sigStart == null || sigEnd == null ||
                nameStart == null || nameEnd == null ||
                !(sigStart <= nameStart && nameStart <= nameEnd && nameEnd <= sigEnd) ||
                sigEnd > content.Length)
                continue;

            var beforeLen = nameStart.Value - sigStart.Value;
            var afterLen = sigEnd.Value - nameEnd.Value;
            var normalizedSig = new byte[beforeLen + DeclaredNamePlaceholder.Length + afterLen];
            var pos = 0;
            Buffer.BlockCopy(content, sigStart.Value, normalizedSig, pos, beforeLen);
            pos += beforeLen;
            Buffer.BlockCopy(DeclaredNamePlaceholder, 0, normalizedSig, pos, DeclaredNamePlaceholder.Length);
            pos += DeclaredNamePlaceholder.Length;
            if (afterLen > 0) Buffer.BlockCopy(content, nameEnd.Value, normalizedSig, pos, afterLen);

            var normalizedSigHash = SHA256.HashData(normalizedSig);

            byte[]? bodyHash = null;
            if (bodyStart != null && bodyEnd != null && bodyEnd <= content.Length && bodyStart < bodyEnd)
            {
                var bodyLen = bodyEnd.Value - bodyStart.Value;
                bodyHash = SHA256.HashData(content.AsSpan(bodyStart.Value, bodyLen));
            }

            if (!declarationsBySymbol.TryGetValue(symbolId, out var list))
            {
                list = [];
                declarationsBySymbol[symbolId] = list;
            }

            list.Add(new DeclarationFingerprint(documentPath, normalizedSigHash, bodyHash));
        }

        var results = new List<SymbolTransitionCandidate>();
        foreach (var symbolId in symbolIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!symbolMeta.TryGetValue(symbolId, out var meta))
                continue;
            if (!declarationsBySymbol.TryGetValue(symbolId, out var decls) || decls.Count == 0)
                continue;

            decls.Sort((a, b) => string.Compare(a.DocumentPath, b.DocumentPath, StringComparison.Ordinal));
            results.Add(new SymbolTransitionCandidate(
                symbolId, meta.Kind, meta.AssemblyIdentity, meta.Fqn, decls));
        }

        return results;
    }

    internal DeclarationOutlinePage? GetDeclarationsOutline(string relativePath, string snapshotId, bool includeGenerated, int limit, OutlineCursor? cursor)
    {
        if (string.IsNullOrEmpty(relativePath) || string.IsNullOrEmpty(snapshotId) || limit <= 0)
            return null;

        var fingerprint = OutlineCursor.ComputeFingerprint(relativePath, includeGenerated);
        if (cursor != null)
        {
            cursor.Validate(snapshotId, fingerprint);
        }

        // Resolve document version and line_starts for this snapshot/document.
        string? docVersionId;
        int[]? lineStarts;
        using (var docCmd = _connection.CreateCommand())
        {
            docCmd.CommandText = """
                SELECT dv.document_version_id, dv.line_starts
                FROM snapshot_documents sd
                JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
                JOIN documents doc ON doc.document_id = dv.document_id
                WHERE sd.snapshot_id = @snapshotId AND doc.relative_path = @relativePath
                LIMIT 1;
                """;
            docCmd.Parameters.AddWithValue("@snapshotId", snapshotId);
            docCmd.Parameters.AddWithValue("@relativePath", relativePath);
            using var reader = docCmd.ExecuteReader();
            if (!reader.Read())
                return null;
            docVersionId = reader.GetString(0);
            lineStarts = SourceLineMap.ParseLineStarts(reader.IsDBNull(1) ? null : reader.GetString(1), docVersionId);
        }

        var owners = ProjectFacts.Load(_connection, snapshotId).GetDocumentOwners(relativePath);
        // Pick one representative for each declaration before counting or applying the cursor.
        var outlineRows = $"""
            WITH outline_rows AS (
                SELECT d.symbol_id, s.kind, ss.fqn, d.full_start, d.full_end, d.signature_start, d.name_start,
                       COALESCE(d.is_partial, 0) AS is_partial, COALESCE(d.is_generated, 0) AS is_generated,
                       ROW_NUMBER() OVER (
                           PARTITION BY d.full_start, d.full_end, s.doc_comment_id
                           ORDER BY (
                               s.assembly_identity COLLATE {AssemblySimpleNameCollation}
                               IN (SELECT value FROM json_each(@owners))
                           ) DESC, d.symbol_id
                       ) AS copy_rank
                FROM declarations d
                {SnapshotDeclarationLink.Join("d", "sdecl")}
                JOIN symbols s ON s.symbol_id = d.symbol_id
                JOIN snapshot_symbols ss ON ss.symbol_id = s.symbol_id AND ss.snapshot_id = sdecl.snapshot_id
                WHERE d.document_version_id = @docVersionId
                  AND d.full_start IS NOT NULL AND d.full_end IS NOT NULL
                  AND {SymbolMetadata.NotImplicitlyDeclaredSql("ss")}
                  {(includeGenerated ? string.Empty : "AND COALESCE(d.is_generated, 0) = 0")}
            )
            """;

        void BindOutlineParameters(SqliteCommand command)
        {
            command.Parameters.AddWithValue("@snapshotId", snapshotId);
            command.Parameters.AddWithValue("@docVersionId", docVersionId);
            SqlIdList.Bind(command, "@owners", owners);
        }

        int totalCount;
        using (var countCmd = _connection.CreateCommand())
        {
            countCmd.CommandText = outlineRows + " SELECT COUNT(*) FROM outline_rows WHERE copy_rank = 1;";
            BindOutlineParameters(countCmd);
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using var cmd = _connection.CreateCommand();
        var whereCursor = cursor != null
            ? "AND (full_start, symbol_id, full_end) > (@lastFullStart, @lastSymbolId, @lastFullEnd)"
            : string.Empty;
        cmd.CommandText = outlineRows + "\n" + $"""
            SELECT symbol_id, kind, fqn, full_start, full_end, signature_start, name_start, is_partial, is_generated
            FROM outline_rows
            WHERE copy_rank = 1
            {whereCursor}
            ORDER BY full_start, symbol_id, full_end
            LIMIT @limitPlusOne;
            """;
        BindOutlineParameters(cmd);
        cmd.Parameters.AddWithValue("@limitPlusOne", limit + 1);
        if (cursor != null)
        {
            cmd.Parameters.AddWithValue("@lastFullStart", cursor.LastFullStart);
            cmd.Parameters.AddWithValue("@lastSymbolId", cursor.LastSymbolId);
            cmd.Parameters.AddWithValue("@lastFullEnd", cursor.LastFullEnd);
        }

        var rows = new List<DeclarationOutlineEntry>();
        var lastFullEnd = 0;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var symbolId = reader.GetString(0);
                var kind = reader.GetString(1);
                var fqn = reader.IsDBNull(2) ? null : reader.GetString(2);
                var fullStart = reader.GetInt32(3);
                var fullEnd = reader.GetInt32(4);
                if (rows.Count < limit)
                    lastFullEnd = fullEnd;
                var sigStart = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
                var nameStart = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
                var isPartial = reader.GetInt32(7) == 1;
                var isGenerated = reader.GetInt32(8) == 1;

                int startLine, endLine;
                int? sigStartLine = null, nameStartLine = null;
                if (lineStarts is { Length: > 0 } && fullStart >= 0 && fullEnd >= fullStart)
                {
                    var sIdx = SourceLineMap.FindLineIndex(lineStarts, fullStart);
                    var eIdx = SourceLineMap.FindEndLineIndex(lineStarts, fullStart, fullEnd);
                    startLine = LineNumbers.ToOneBased(sIdx);
                    endLine = LineNumbers.ToOneBased(eIdx);
                    if (sigStart.HasValue)
                        sigStartLine = LineNumbers.ToOneBased(SourceLineMap.FindLineIndex(lineStarts, sigStart.Value));
                    if (nameStart.HasValue)
                        nameStartLine = LineNumbers.ToOneBased(SourceLineMap.FindLineIndex(lineStarts, nameStart.Value));
                }
                else
                {
                    // Degraded: no line_starts, cannot map — emit 0 and let caller know via is_generated etc.
                    // This path should not happen on a normal index (line_starts populated for all docs).
                    startLine = 0;
                    endLine = 0;
                }

                rows.Add(new DeclarationOutlineEntry(symbolId, kind, fqn, startLine, endLine, sigStartLine, nameStartLine, isPartial, isGenerated, fullStart));
            }
        }

        string? nextCursor = null;
        if (rows.Count > limit)
        {
            // Last row is the lookahead; drop it and encode the cursor from the last returned row.
            rows.RemoveAt(rows.Count - 1);
            var last = rows[^1];
            nextCursor = new OutlineCursor(snapshotId, fingerprint, last.FullStart, last.SymbolId, lastFullEnd).Encode();
        }

        return new DeclarationOutlinePage(rows, nextCursor, totalCount);
    }

    private static string? DeriveParentTypeDocCommentId(string docCommentId)
    {
        return SymbolId.DeriveContainingTypeDocCommentId(docCommentId);
    }

    private sealed record SymbolSpanContent(byte[]? Content, int? Start, int? End, int[]? LineStarts, string DocumentVersionId);
}