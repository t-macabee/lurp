using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lurp.Storage;

internal sealed class DeclarationReadStore(SqliteConnection connection)
{
    private static readonly byte[] DeclaredNamePlaceholder = "<DECLARED_NAME>"u8.ToArray();
    private readonly SqliteConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    internal IndexedSymbolInfo? GetSymbolInfo(string symbolId, string snapshotId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind, ss.fqn, ss.metadata_json,
                   (SELECT COUNT(*) FROM declarations d
                    JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                    WHERE d.symbol_id = s.symbol_id AND sd.snapshot_id = @snapshotId) AS decl_count,
                   (SELECT MAX(d.is_partial) FROM declarations d
                    JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                    WHERE d.symbol_id = s.symbol_id AND sd.snapshot_id = @snapshotId) AS is_partial
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
            case ViewKind.Name:
                startCol = "name_start";
                endCol = "name_end";
                break;
            // default: ContainingType/Surrounding intentionally routed to GetContainingTypeSource/GetSurroundingLines — throw is intentional
            default:
                throw new ArgumentOutOfRangeException(nameof(viewKind), viewKind,
                    "Use GetContainingTypeSource or GetSurroundingLines for this view kind.");
        }

        var views = GetSymbolSpanContents(symbolId, snapshotId, startCol, endCol, includeGenerated)
            .Where(static span => span is { Content: not null, Start: not null, End: not null })
            .Select(span => SliceToString(span.Content!, span.Start!.Value, span.End!.Value))
            .Where(static source => source != null)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return views.Count == 0 ? null : string.Join("\n\n", views);
    }

    internal string? GetContainingTypeSource(string symbolId, string snapshotId)
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

        var parentSymbolId = $"{parentDocCommentId}|{assemblyIdentity}";

        return GetSymbolSource(parentSymbolId, snapshotId, ViewKind.Declaration);
    }

    internal string? GetSurroundingLines(string symbolId, string snapshotId, int contextLines)
    {
        var views = new List<string>();
        foreach (var span in GetSymbolSpanContents(symbolId, snapshotId, "full_start", "full_end"))
        {
            if (span.Content == null || span.Start == null || span.End == null || span.LineStarts is not { Length: > 0 })
                continue;

            // These are 0-based indexes into line_starts (never consumer-facing
            // line numbers); names carry the Index suffix to keep the boundary
            // with the 1-based DeclarationLocation values self-documenting.
            var startLineIndex = FindLineIndex(span.LineStarts, span.Start.Value);
            var endLineIndex = FindLineIndex(span.LineStarts, span.End.Value - 1);
            var expandedStartLineIndex = Math.Max(0, startLineIndex - contextLines);
            var expandedEndLineIndex = Math.Min(span.LineStarts.Length - 1, endLineIndex + contextLines);
            var byteStart = span.LineStarts[expandedStartLineIndex];
            var byteEnd = expandedEndLineIndex + 1 < span.LineStarts.Length
                ? span.LineStarts[expandedEndLineIndex + 1]
                : span.Content.Length;
            var source = SliceToString(span.Content, byteStart, byteEnd);
            if (source != null && !views.Contains(source, StringComparer.Ordinal))
                views.Add(source);
        }

        return views.Count == 0 ? null : string.Join("\n\n", views);
    }

    internal List<DeclarationLocation> GetDeclarationLocations(string symbolId, string snapshotId, bool includeGenerated = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT doc.relative_path, d.full_start, d.full_end, dv.line_starts, dv.content,
                   COALESCE(d.is_generated, 0)
            FROM declarations d
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId AND d.symbol_id = @symbolId
            """;
        if (!includeGenerated)
            command.CommandText += " AND COALESCE(d.is_generated, 0) = 0";
        command.CommandText += " ORDER BY doc.relative_path, d.full_start;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@symbolId", symbolId);

        var results = new List<DeclarationLocation>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4))
                continue;
            var start = reader.GetInt32(1);
            var end = reader.GetInt32(2);
            var lineStarts = JsonSerializer.Deserialize<int[]>(reader.GetString(3));
            var content = (byte[])reader[4];
            if (lineStarts is not { Length: > 0 } || start < 0 || end < start || end > content.Length)
                continue;
            // FindLineIndex returns a 0-based index into line_starts; storage is
            // Roslyn-native 0-based. The 0-to-1 conversion happens ONLY here via the
            // LineNumbers choke point, so the DeclarationLocation a consumer reads
            // is 1-based (matching --line=). The raw indexes still index line_starts.
            var startLineIndex = FindLineIndex(lineStarts, start);
            var endLineIndex = FindLineIndex(lineStarts, end);
            var startLine = LineNumbers.ToOneBased(startLineIndex);
            var endLine = LineNumbers.ToOneBased(endLineIndex);
            results.Add(new DeclarationLocation(
                reader.GetString(0),
                startLine,
                Utf8Column(content, lineStarts[startLineIndex], start),
                endLine,
                Utf8Column(content, lineStarts[endLineIndex], end),
                reader.GetInt32(5) == 1));
        }

        return results;
    }

    private static int Utf8Column(byte[] content, int lineStart, int offset)
    {
        var safeOffset = Math.Clamp(offset, lineStart, content.Length);
        return Encoding.UTF8.GetCharCount(content, lineStart, safeOffset - lineStart);
    }

    private List<SymbolSpanContent> GetSymbolSpanContents(string symbolId, string snapshotId, string startCol, string endCol, bool includeGenerated = false)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = $"""
            SELECT dv.content, d.{startCol}, d.{endCol}, dv.line_starts
            FROM snapshot_symbols ss
            JOIN declarations d ON d.symbol_id = ss.symbol_id
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE ss.snapshot_id = @snapshotId
              AND sd.snapshot_id = @snapshotId
              AND ss.symbol_id = @symbolId
            """;

        if (!includeGenerated) command.CommandText += " AND (d.is_generated = 0 OR d.is_generated IS NULL)";

        command.CommandText += " ORDER BY doc.relative_path, d.full_start;";

        command.Parameters.AddWithValue("@symbolId", symbolId);
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var results = new List<SymbolSpanContent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var lineStarts = reader.IsDBNull(3)
                ? null
                : JsonSerializer.Deserialize<int[]>(reader.GetString(3));
            results.Add(new SymbolSpanContent(
                reader.IsDBNull(0) ? null : (byte[])reader[0],
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                lineStarts));
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

    private static int FindLineIndex(int[] lineStarts, int byteOffset)
    {
        int lo = 0, hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= byteOffset)
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    private static string? SliceToString(byte[] content, int start, int end)
    {
        if (start < 0 || end > content.Length || start > end)
            return null;
        var length = end - start;
        if (length == 0)
            return string.Empty;
        return Encoding.UTF8.GetString(content, start, length);
    }

    internal IReadOnlyList<SymbolTransitionCandidate> LoadTransitionCandidates(
        string snapshotId,
        IReadOnlyCollection<string> symbolIds)
    {
        if (symbolIds.Count == 0)
            return [];

        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.kind, s.assembly_identity, ss.fqn,
                   d.signature_start, d.signature_end,
                   d.name_start, d.name_end,
                   d.body_start, d.body_end,
                   doc.relative_path, dv.content
            FROM snapshot_symbols ss
            JOIN symbols s ON s.symbol_id = ss.symbol_id
            JOIN declarations d ON d.symbol_id = ss.symbol_id
            JOIN snapshot_documents sd ON sd.document_version_id = d.document_version_id
                AND sd.snapshot_id = ss.snapshot_id
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents doc ON doc.document_id = dv.document_id
            WHERE ss.snapshot_id = @snapshotId
              AND s.symbol_id IN (@symbolIds)
              AND (d.is_generated = 0 OR d.is_generated IS NULL)
            ORDER BY s.symbol_id, doc.relative_path, d.signature_start;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var paramNames = new List<string>();
        var idx = 0;
        foreach (var symbolId in symbolIds)
        {
            var paramName = $"@sid{idx++}";
            paramNames.Add(paramName);
            command.Parameters.AddWithValue(paramName, symbolId);
        }

        command.CommandText = command.CommandText.Replace("@symbolIds", string.Join(",", paramNames));

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

    private static string? DeriveParentTypeDocCommentId(string docCommentId)
    {
        return SymbolId.DeriveContainingTypeDocCommentId(docCommentId);
    }

    private sealed record SymbolSpanContent(byte[]? Content, int? Start, int? End, int[]? LineStarts);
}