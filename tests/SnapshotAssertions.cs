using System;
using System.Collections.Generic;
using System.Linq;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Shared snapshot-comparison helpers used by both CleanRebuildEquivalenceTest
/// and RealSolutionIntegrationTests. Extracted to avoid duplication across the
/// two equivalence-test classes.
/// </summary>
internal static class SnapshotAssertions
{
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

    private sealed record SemanticChangeSnapshot(
        string ChangeType,
        string SymbolId,
        string? DetailJson);

    public static void CompareSnapshotsAreEquivalent(
        string dbPath, string snapshotB, string snapshotC)
    {
        Assert.NotEqual(snapshotB, snapshotC);

        var store = new SqliteIndexStore(dbPath);
        store.Open();

        try
        {
            var symbolsB = store.GetSymbolIdsInSnapshot(snapshotB);
            var symbolsC = store.GetSymbolIdsInSnapshot(snapshotC);
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
                ReadSymbols(dbPath, snapshotC),
                ReadSymbols(dbPath, snapshotB));

            Assert.Equal(
                ReadDeclarations(dbPath, snapshotC),
                ReadDeclarations(dbPath, snapshotB));

            var edgesB = store.GetEdges(snapshotB);
            var edgesC = store.GetEdges(snapshotC);
            NormalizeEdges(edgesB);
            NormalizeEdges(edgesC);

            if (edgesC.Count != edgesB.Count)
            {
                var bSet = edgesB.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
                var cSet = edgesC.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
                Assert.Fail($"Edge count mismatch: {edgesB.Count} (B:incremental) vs {edgesC.Count} (C:full rebuild).\n" +
                    $"Only in B: {string.Join(", ", bSet.Except(cSet).Take(10))}\n" +
                    $"Only in C: {string.Join(", ", cSet.Except(bSet).Take(10))}");
            }

            for (int i = 0; i < edgesC.Count && i < edgesB.Count; i++)
            {
                AssertEqual(edgesB[i], edgesC[i]);
            }

            var diagB = store.GetDiagnostics(snapshotB);
            var diagC = store.GetDiagnostics(snapshotC);
            NormalizeDiagnostics(diagB);
            NormalizeDiagnostics(diagC);

            Assert.Equal(diagC.Count, diagB.Count);
            for (int i = 0; i < diagC.Count && i < diagB.Count; i++)
            {
                AssertEqual(diagB[i], diagC[i]);
            }

            var incompletenessB = store.GetBindingIncompleteness(snapshotB);
            var incompletenessC = store.GetBindingIncompleteness(snapshotC);
            Assert.Equal(incompletenessC, incompletenessB);

            var annB = store.GetAnnotations(snapshotB);
            var annC = store.GetAnnotations(snapshotC);
            NormalizeAnnotations(annB);
            NormalizeAnnotations(annC);

            Assert.Equal(annC.Count, annB.Count);
            for (int i = 0; i < annC.Count && i < annB.Count; i++)
            {
                AssertEqual(annB[i], annC[i]);
            }

            var ftsCountsB = GetFtsCounts(dbPath, snapshotB);
            var ftsCountsC = GetFtsCounts(dbPath, snapshotC);
            Assert.Equal(ftsCountsC.SourceRows, ftsCountsB.SourceRows);
            Assert.Equal(ftsCountsC.SymbolRows, ftsCountsB.SymbolRows);

            Assert.Equal(
                ReadSourceFts(dbPath, snapshotC),
                ReadSourceFts(dbPath, snapshotB));

            Assert.Equal(
                ReadSymbolFts(dbPath, snapshotC),
                ReadSymbolFts(dbPath, snapshotB));

            // Semantic changes: compare ordinally sorted (ChangeType, SymbolId, canonical DetailJson)
            // projections, deliberately excluding ChangeId, FromSnapshotId, ToSnapshotId, and CreatedAtUtc
            // which differ between otherwise equivalent runs.
            // Only compare when both snapshots have the same number of semantic changes from the same
            // from_snapshot_id. Incremental-vs-full runs and first-run vs subsequent runs legitimately
            // differ in change count or from_snapshot, so they skip this check.
            var changesB = ReadSemanticChanges(dbPath, snapshotB);
            var changesC = ReadSemanticChanges(dbPath, snapshotC);
            if (changesB.Count == changesC.Count && changesB.Count > 0)
            {
                var fromB = GetSemanticDiffFromSnapshot(dbPath, snapshotB);
                var fromC = GetSemanticDiffFromSnapshot(dbPath, snapshotC);
                if (fromB != null && fromB == fromC)
                    Assert.Equal(changesC, changesB);
            }
        }
        finally
        {
            store.Close();
        }
    }

    private static List<SymbolSnapshot> ReadSymbols(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT symbol_id, fqn, metadata_json
            FROM snapshot_symbols
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SymbolSnapshot(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return result;
    }

    private static List<DeclarationSnapshot> ReadDeclarations(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
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
                     d.name_start, d.name_end;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<DeclarationSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
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
        }

        return result;
    }

    private static List<SourceFtsSnapshot> ReadSourceFts(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT document_path, content
            FROM source_fts
            WHERE snapshot_id = @snapshotId
            ORDER BY document_path, content;";
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
        command.CommandText = @"
            SELECT symbol_id, fqn, doc_comment_id, kind
            FROM symbol_fts
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id, fqn, doc_comment_id, kind;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolFtsSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SymbolFtsSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }
        return result;
    }

    private static List<SemanticChangeSnapshot> ReadSemanticChanges(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT change_type, symbol_id, detail_json
            FROM semantic_changes
            WHERE to_snapshot_id = @snapshotId
            ORDER BY change_type, symbol_id, detail_json;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SemanticChangeSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new SemanticChangeSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return result;
    }

    /// <summary>
    /// Return the from_snapshot_id for semantic_changes targeting the given snapshot,
    /// or null if none exist. Used to decide whether semantic_changes are comparable.
    /// </summary>
    private static string? GetSemanticDiffFromSnapshot(string dbPath, string snapshotId)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT DISTINCT from_snapshot_id
            FROM semantic_changes
            WHERE to_snapshot_id = @snapshotId;";
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = command.ExecuteScalar();
        return result as string;
    }

    private static int? ReadNullableInt(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static void NormalizeEdges(List<EdgeRecord> edges)
    {
        foreach (var edge in edges)
        {
            var field = typeof(EdgeRecord).GetField("<SnapshotId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field != null)
                field.SetValue(edge, string.Empty);
        }

        edges.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.SourceSymbolId, b.SourceSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TargetSymbolId, b.TargetSymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Provenance, b.Provenance);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.SourceDocumentPath ?? "", b.SourceDocumentPath ?? "");
            if (cmp != 0) return cmp;
            return (a.SourceStartLine ?? 0).CompareTo(b.SourceStartLine ?? 0);
        });
    }

    public static void NormalizeDiagnostics(List<DiagnosticRecord> diags)
    {
        diags.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.DocumentPath ?? "", b.DocumentPath ?? "");
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Id, b.Id);
            if (cmp != 0) return cmp;
            return (a.StartLine ?? 0).CompareTo(b.StartLine ?? 0);
        });
    }

    public static void NormalizeAnnotations(List<AnnotationRecord> annotations)
    {
        annotations.Sort((a, b) =>
        {
            int cmp = StringComparer.Ordinal.Compare(a.SymbolId, b.SymbolId);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.Kind, b.Kind);
        });
    }

    public static void AssertEqual(EdgeRecord expected, EdgeRecord actual)
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
    }

    public static void AssertEqual(DiagnosticRecord expected, DiagnosticRecord actual)
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

    public static void AssertEqual(AnnotationRecord expected, AnnotationRecord actual)
    {
        Assert.Equal(expected.SymbolId, actual.SymbolId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Value, actual.Value);
    }

    /// <summary>
    /// Return (source_fts rows, symbol_fts rows) for a snapshot.
    /// Opens a fresh connection so the read is independent of any store lifecycle.
    /// </summary>
    public static (int SourceRows, int SymbolRows) GetFtsCounts(string dbPath, string snapshotId)
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

    public static void SqliteConnectionClearAllPools()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
