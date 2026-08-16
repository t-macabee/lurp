using Lurp.Storage;
using Microsoft.Data.Sqlite;
using System.Reflection;

namespace Lurp.Tests;

internal static class SnapshotAssertions
{
    public static void CompareSnapshotsAreEquivalent(
        string dbPathB, string snapshotB, string dbPathC, string snapshotC)
    {
        Assert.Equal(snapshotB, snapshotC);

        using var storeB = OpenStore(dbPathB);
        using var storeC = OpenStore(dbPathC);

        try
        {
            var symbolsB = storeB.GetSymbolIdsInSnapshot(snapshotB);
            var symbolsC = storeC.GetSymbolIdsInSnapshot(snapshotC);
            symbolsB.Sort(StringComparer.Ordinal);
            symbolsC.Sort(StringComparer.Ordinal);

            Assert.Equal(symbolsC.Count, symbolsB.Count);
            Assert.True(
                symbolsB.SequenceEqual(symbolsC, StringComparer.Ordinal),
                $"Symbol set mismatch between B ({snapshotB}) and C ({snapshotC}).\n" +
                $"  B count: {symbolsB.Count}, C count: {symbolsC.Count}\n" +
                $"  Only in B: {string.Join(", ", symbolsB.Except(symbolsC, StringComparer.Ordinal).Take(10))}\n" +
                $"  Only in C: {string.Join(", ", symbolsC.Except(symbolsB, StringComparer.Ordinal).Take(10))}");

            Assert.Equal(
                ReadSymbols(dbPathC, snapshotC),
                ReadSymbols(dbPathB, snapshotB));

            Assert.Equal(
                ReadDeclarations(dbPathC, snapshotC),
                ReadDeclarations(dbPathB, snapshotB));

            var edgesB = storeB.GetEdges(snapshotB);
            var edgesC = storeC.GetEdges(snapshotC);
            NormalizeEdges(edgesB);
            NormalizeEdges(edgesC);

            if (edgesC.Count != edgesB.Count)
            {
                var bSet = edgesB.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}")
                    .ToHashSet();
                var cSet = edgesC.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}")
                    .ToHashSet();
                Assert.Fail(
                    $"Edge count mismatch: {edgesB.Count} (B:incremental) vs {edgesC.Count} (C:full rebuild).\n" +
                    $"Only in B: {string.Join(", ", bSet.Except(cSet).Take(10))}\n" +
                    $"Only in C: {string.Join(", ", cSet.Except(bSet).Take(10))}");
            }

            for (var i = 0; i < edgesC.Count && i < edgesB.Count; i++) AssertEqual(edgesB[i], edgesC[i]);

            var diagB = storeB.GetDiagnostics(snapshotB);
            var diagC = storeC.GetDiagnostics(snapshotC);
            NormalizeDiagnostics(diagB);
            NormalizeDiagnostics(diagC);

            Assert.Equal(diagC.Count, diagB.Count);
            for (var i = 0; i < diagC.Count && i < diagB.Count; i++) AssertEqual(diagB[i], diagC[i]);

            var incompletenessB = storeB.GetBindingIncompleteness(snapshotB);
            var incompletenessC = storeC.GetBindingIncompleteness(snapshotC);
            Assert.Equal(incompletenessC, incompletenessB);

            var annB = storeB.GetAnnotations(snapshotB);
            var annC = storeC.GetAnnotations(snapshotC);
            NormalizeAnnotations(annB);
            NormalizeAnnotations(annC);

            Assert.Equal(annC.Count, annB.Count);
            for (var i = 0; i < annC.Count && i < annB.Count; i++) AssertEqual(annB[i], annC[i]);

            var ftsCountsB = GetFtsCounts(dbPathB, snapshotB);
            var ftsCountsC = GetFtsCounts(dbPathC, snapshotC);
            Assert.Equal(ftsCountsC.SourceRows, ftsCountsB.SourceRows);
            Assert.Equal(ftsCountsC.SymbolRows, ftsCountsB.SymbolRows);

            Assert.Equal(
                ReadSourceFts(dbPathC, snapshotC),
                ReadSourceFts(dbPathB, snapshotB));

            Assert.Equal(
                ReadSymbolFts(dbPathC, snapshotC),
                ReadSymbolFts(dbPathB, snapshotB));
        }
        finally
        {
            storeB.Close();
            storeC.Close();
        }
    }

    private static SqliteIndexStore OpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        return store;
    }

    private static List<SymbolSnapshot> ReadSymbols(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, fqn, metadata_json
            FROM snapshot_symbols
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SymbolSnapshot(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));

        return result;
    }

    private static List<DeclarationSnapshot> ReadDeclarations(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.symbol_id, docs.relative_path,
                   d.full_start, d.full_end,
                   d.signature_start, d.signature_end,
                   d.body_start, d.body_end,
                   d.name_start, d.name_end,
                   d.is_partial, d.is_generated, d.generator_identity
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents docs ON docs.document_id = dv.document_id
            JOIN declarations d ON d.document_version_id = sd.document_version_id
            WHERE sd.snapshot_id = @snapshotId
            ORDER BY d.symbol_id, docs.relative_path, d.full_start, d.full_end,
                     d.signature_start, d.signature_end, d.body_start, d.body_end,
                     d.name_start, d.name_end;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<DeclarationSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new DeclarationSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                ReadNullableInt(reader, 2),
                ReadNullableInt(reader, 3),
                ReadNullableInt(reader, 4),
                ReadNullableInt(reader, 5),
                ReadNullableInt(reader, 6),
                ReadNullableInt(reader, 7),
                ReadNullableInt(reader, 8),
                ReadNullableInt(reader, 9),
                reader.GetInt32(10) != 0,
                reader.GetInt32(11) != 0,
                reader.IsDBNull(12) ? null : reader.GetString(12)));

        return result;
    }

    private static List<SourceFtsSnapshot> ReadSourceFts(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document_path, content
            FROM source_fts
            WHERE snapshot_id = @snapshotId
            ORDER BY document_path, content;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SourceFtsSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SourceFtsSnapshot(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static List<SymbolFtsSnapshot> ReadSymbolFts(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, fqn, doc_comment_id, kind
            FROM symbol_fts
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id, fqn, doc_comment_id, kind;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolFtsSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SymbolFtsSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        return result;
    }

    private static int? ReadNullableInt(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static void NormalizeEdges(List<EdgeRecord> edges)
    {
        foreach (var edge in edges)
        {
            var field = typeof(EdgeRecord).GetField("<SnapshotId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (field != null)
                field.SetValue(edge, string.Empty);
        }

        edges.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.SourceSymbolId, b.SourceSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TargetSymbolId, b.TargetSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Provenance, b.Provenance);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.SourceDocumentPath ?? "", b.SourceDocumentPath ?? "");
            if (cmp != 0) return cmp;
            cmp = (a.SourceStartLine ?? 0).CompareTo(b.SourceStartLine ?? 0);
            if (cmp != 0) return cmp;
            cmp = (a.SourceStartColumn ?? 0).CompareTo(b.SourceStartColumn ?? 0);
            if (cmp != 0) return cmp;
            cmp = (a.SourceEndLine ?? 0).CompareTo(b.SourceEndLine ?? 0);
            if (cmp != 0) return cmp;
            cmp = (a.SourceEndColumn ?? 0).CompareTo(b.SourceEndColumn ?? 0);
            if (cmp != 0) return cmp;
            cmp = a.IsCrossGenerated.CompareTo(b.IsCrossGenerated);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TypeArgumentsJson ?? "", b.TypeArgumentsJson ?? "");
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.ReceiverTypeConstraintsJson ?? "",
                b.ReceiverTypeConstraintsJson ?? "");
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.SourceNodeKind?.ToString() ?? "", b.SourceNodeKind?.ToString() ?? "");
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.TargetNodeKind?.ToString() ?? "", b.TargetNodeKind?.ToString() ?? "");
        });
    }

    private static void NormalizeDiagnostics(List<DiagnosticRecord> diags)
    {
        diags.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.DocumentPath ?? "", b.DocumentPath ?? "");
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Id, b.Id);
            if (cmp != 0) return cmp;
            return (a.StartLine ?? 0).CompareTo(b.StartLine ?? 0);
        });
    }

    private static void NormalizeAnnotations(List<AnnotationRecord> annotations)
    {
        annotations.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.SymbolId, b.SymbolId);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.Kind, b.Kind);
        });
    }

    private static void AssertEqual(EdgeRecord expected, EdgeRecord actual)
    {
        Assert.Equal(expected.SourceSymbolId, actual.SourceSymbolId);
        Assert.Equal(expected.TargetSymbolId, actual.TargetSymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Provenance, actual.Provenance);
        Assert.Equal(expected.SnapshotId ?? "", actual.SnapshotId ?? "");
        Assert.Equal(expected.ExtractorVersion ?? "", actual.ExtractorVersion ?? "");
        Assert.Equal(expected.SourceDocumentPath ?? "", actual.SourceDocumentPath ?? "");
        Assert.Equal(expected.SourceStartLine, actual.SourceStartLine);
        Assert.Equal(expected.SourceEndLine, actual.SourceEndLine);
        Assert.Equal(expected.SourceStartColumn, actual.SourceStartColumn);
        Assert.Equal(expected.SourceEndColumn, actual.SourceEndColumn);
        Assert.Equal(expected.ReceiverTypeConstraintsJson, actual.ReceiverTypeConstraintsJson);
        Assert.Equal(expected.IsCrossGenerated, actual.IsCrossGenerated);
        Assert.Equal(expected.TypeArgumentsJson, actual.TypeArgumentsJson);
        Assert.Equal(expected.SourceNodeKind, actual.SourceNodeKind);
        Assert.Equal(expected.TargetNodeKind, actual.TargetNodeKind);
    }

    private static void AssertEqual(DiagnosticRecord expected, DiagnosticRecord actual)
    {
        Assert.Equal(expected.ProjectName, actual.ProjectName);
        Assert.Equal(expected.DocumentPath, actual.DocumentPath);
        Assert.Equal(expected.Severity, actual.Severity);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.StartLine, actual.StartLine);
        Assert.Equal(expected.StartColumn, actual.StartColumn);
        Assert.Equal(expected.EndLine, actual.EndLine);
        Assert.Equal(expected.EndColumn, actual.EndColumn);
    }

    private static void AssertEqual(AnnotationRecord expected, AnnotationRecord actual)
    {
        Assert.Equal(expected.SymbolId, actual.SymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Value, actual.Value);
    }

    private static (int SourceRows, int SymbolRows) GetFtsCounts(string dbPath, string snapshotId)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM source_fts WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        var sourceRows = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

        cmd.CommandText = "SELECT COUNT(*) FROM symbol_fts WHERE snapshot_id = @id;";
        var symbolRows = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);

        return (sourceRows, symbolRows);
    }

    private sealed record SymbolSnapshot(string SymbolId, string? FullyQualifiedName, string? MetadataJson);

    private sealed record DeclarationSnapshot(
        string SymbolId,
        string DocumentPath,
        int? FullStart,
        int? FullEnd,
        int? SignatureStart,
        int? SignatureEnd,
        int? BodyStart,
        int? BodyEnd,
        int? NameStart,
        int? NameEnd,
        bool IsPartial,
        bool IsGenerated,
        string? GeneratorIdentity);

    private sealed record SourceFtsSnapshot(string DocumentPath, string Content);

    private sealed record SymbolFtsSnapshot(
        string SymbolId,
        string Fqn,
        string DocCommentId,
        string Kind);
}