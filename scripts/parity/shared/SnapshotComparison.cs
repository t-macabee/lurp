using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Parity.Shared;

public static class SnapshotComparison
{
    // Snapshot keys scope the reads. Other excluded IDs link rows or identify rows within one database.
    private static readonly Dictionary<string, (string[] Compared, string[] Excluded)> ComparedTables = new(StringComparer.Ordinal)
    {
        ["snapshots"] = (
            ["status", "payload_pruned", "failure_reason_code", "tool_version", "database_schema_version",
             "output_schema_version", "extractor_version", "sdk_version", "compiler_version", "skipped_adapters",
             "analyzers_skipped"],
            ["snapshot_id", // The comparison checks the requested IDs separately.
             "built_at_utc", "selected_at_utc", "build_strategy", "previous_snapshot_id", // Index history
             "workspace_id", // Database identity
             "failure_message"]), // The comparison uses the failure reason code.
        ["snapshot_symbols"] = (["symbol_id", "fqn", "metadata_json", "bare_fqn"], ["snapshot_id"]),
        ["symbols"] = (["symbol_id", "doc_comment_id", "assembly_identity", "kind"], []),
        ["snapshot_documents"] = ([], ["snapshot_id", "document_version_id"]),
        ["documents"] = (["relative_path"], ["document_id"]),
        ["document_versions"] = (
            ["content_hash", "is_generated", "line_starts", "encoding", "byte_count", "content"],
            ["document_version_id", "document_id"]),
        ["snapshot_declarations"] = ([], ["snapshot_id", "declaration_id"]),
        ["declarations"] = (
            ["symbol_id", "full_start", "full_end", "signature_start", "signature_end", "body_start", "body_end",
             "name_start", "name_end", "is_partial", "is_generated", "generator_identity"],
            ["declaration_id", "document_version_id"]),
        ["edges"] = (
            ["source_symbol_id", "target_symbol_id", "kind", "provenance", "extractor_version", "source_document_path",
             "source_start_line", "source_start_column", "source_end_line", "source_end_column", "is_cross_generated",
             "type_arguments_json", "receiver_type_constraints_json"],
            ["edge_id", "snapshot_id"]),
        ["edge_sites"] = (["provenance", "document_path", "start_line", "start_column", "end_line", "end_column", "extractor_version", "is_cross_generated"], ["edge_id"]),
        ["diagnostics"] = (
            ["project_name", "document_path", "severity", "id", "message", "start_line", "start_column", "end_line", "end_column"],
            ["diagnostic_id", "snapshot_id"]),
        ["binding_incompleteness"] = (
            ["project_name", "document_path", "reason", "occurrence_count", "extractor_version"], ["snapshot_id"]),
        ["annotations"] = (["symbol_id", "kind", "value", "document_path"], ["annotation_id", "snapshot_id"]),
        ["source_fts"] = (["document_path", "content"], ["snapshot_id", "document_version_id"]),
        ["symbol_fts"] = (
            ["symbol_id", "fqn", "doc_comment_id", "kind", "simple_name", "name_tokens", "container_tokens", "kind_rank", "is_test"],
            ["snapshot_id"]),
        ["snapshot_graph_nodes"] = (["node_id"], ["snapshot_id"]),
        ["snapshot_excluded_symbols"] = (["project_name", "symbol_id"], ["snapshot_id"]),
        ["projects"] = (
            ["name", "assembly_name", "target_framework", "metadata_reference_identities", "compilation_options_fingerprint",
             "document_paths_json", "internals_visible_to", "project_path", "excluded_documents_fingerprint"], ["project_id", "snapshot_id"]),
        ["project_references"] = (["referenced_project_name"], ["project_id"]),
        ["symbol_target_frameworks"] = (["symbol_id", "tfm"], ["snapshot_id"])
    };

    private static readonly HashSet<string> ExcludedTables = new(StringComparer.Ordinal)
    {
        "snapshot_timings", "snapshot_metrics", // Run measurements
        "snapshot_build_inputs", // Freshness metadata can change when a snapshot is reused.
        "snapshot_pins", // User state
        "semantic_changes", // Changes from the prior snapshot depend on index history
        "schema_metadata", // Migration log
        "extractors", // Code constants
        "workspaces", // Database identity
        "sqlite_sequence", "sqlite_stat1", // SQLite internals
        "source_fts_config", "source_fts_content", "source_fts_data", "source_fts_docsize", "source_fts_idx", // source_fts shadow tables
        "symbol_fts_config", "symbol_fts_content", "symbol_fts_data", "symbol_fts_docsize", "symbol_fts_idx" // symbol_fts shadow tables
    };

    public static IReadOnlyList<string> Compare(string dbPathA, string snapshotA, string dbPathB, string snapshotB)
    {
        var differences = new List<string>();
        if (!string.Equals(snapshotA, snapshotB, StringComparison.Ordinal))
            differences.Add($"snapshots: snapshot_id differs (A: {snapshotA}, B: {snapshotB})");

        using var storeA = new SqliteIndexStore(dbPathA);
        using var storeB = new SqliteIndexStore(dbPathB);
        storeA.OpenReadOnly();
        storeB.OpenReadOnly();

        CompareRows("snapshots", ReadSnapshotRow(dbPathA, snapshotA), ReadSnapshotRow(dbPathB, snapshotB));
        CompareRows("symbols", ReadSymbolIdentities(dbPathA, snapshotA), ReadSymbolIdentities(dbPathB, snapshotB));
        CompareRows("snapshot_symbols", ReadSymbols(dbPathA, snapshotA), ReadSymbols(dbPathB, snapshotB));
        CompareRows("snapshot_documents", ReadDocuments(dbPathA, snapshotA), ReadDocuments(dbPathB, snapshotB));
        CompareRows("snapshot_declarations", ReadDeclarations(dbPathA, snapshotA), ReadDeclarations(dbPathB, snapshotB));
        CompareRows("symbol_target_frameworks", ReadSymbolTargetFrameworks(dbPathA, snapshotA),
            ReadSymbolTargetFrameworks(dbPathB, snapshotB));
        CompareRows("edges", ReadEdges(storeA, snapshotA), ReadEdges(storeB, snapshotB));
        CompareRows("edge_sites", ReadEdgeSites(dbPathA, snapshotA), ReadEdgeSites(dbPathB, snapshotB));
        CompareRows("diagnostics", ReadDiagnostics(storeA, snapshotA), ReadDiagnostics(storeB, snapshotB));
        CompareRows("binding_incompleteness", storeA.GetBindingIncompleteness(snapshotA),
            storeB.GetBindingIncompleteness(snapshotB));
        CompareRows("annotations", ReadAnnotations(storeA, snapshotA), ReadAnnotations(storeB, snapshotB));
        CompareRows("source_fts", ReadSourceFts(dbPathA, snapshotA), ReadSourceFts(dbPathB, snapshotB));
        CompareRows("symbol_fts", ReadSymbolFts(dbPathA, snapshotA), ReadSymbolFts(dbPathB, snapshotB));
        CompareRows("snapshot_graph_nodes", ReadSnapshotGraphNodes(dbPathA, snapshotA),
            ReadSnapshotGraphNodes(dbPathB, snapshotB));
        CompareRows("snapshot_excluded_symbols", ReadExcludedSymbols(storeA, snapshotA), ReadExcludedSymbols(storeB, snapshotB));
        CompareRows("projects", ReadProjects(dbPathA, snapshotA), ReadProjects(dbPathB, snapshotB));
        CompareRows("project_references", ReadProjectReferences(dbPathA, snapshotA), ReadProjectReferences(dbPathB, snapshotB));

        differences.AddRange(ReadCoverageDifferences(dbPathA).Union(ReadCoverageDifferences(dbPathB), StringComparer.Ordinal));
        return differences;

        void CompareRows<T>(string table, IReadOnlyList<T> a, IReadOnlyList<T> b)
        {
            var firstDifference = 0;
            while (firstDifference < a.Count && firstDifference < b.Count &&
                   EqualityComparer<T>.Default.Equals(a[firstDifference], b[firstDifference]))
                firstDifference++;

            if (firstDifference == a.Count && firstDifference == b.Count)
                return;

            differences.Add($"{table}: rows differ (A: {a.Count}, B: {b.Count})" +
                            $"\n  A: {Sample(a, firstDifference)}\n  B: {Sample(b, firstDifference)}");
        }
    }

    private static string Sample<T>(IReadOnlyList<T> rows, int offset) =>
        string.Join(" | ", rows.Skip(offset).Take(10).Select(row =>
        {
            var text = row?.ToString() ?? "null";
            return text.Length > 500 ? text[..500] + "…" : text;
        }));

    private static IEnumerable<string> ReadCoverageDifferences(string path)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.name, c.name
            FROM sqlite_master t
            LEFT JOIN pragma_table_info(t.name) c
            WHERE t.type = 'table'
            ORDER BY t.name, c.cid;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var table = reader.GetString(0);
            var comparedTable = ComparedTables.TryGetValue(table, out var columns);
            var excludedTable = ExcludedTables.Contains(table);
            if (comparedTable && excludedTable)
                yield return $"table {table} is both compared and excluded";
            else if (!comparedTable && !excludedTable)
                yield return $"table {table} is neither compared nor excluded";
            else if (comparedTable && !reader.IsDBNull(1))
            {
                var column = reader.GetString(1);
                var compared = columns.Compared.Contains(column, StringComparer.Ordinal);
                var excluded = columns.Excluded.Contains(column, StringComparer.Ordinal);
                if (compared && excluded)
                    yield return $"column {table}.{column} is both compared and excluded";
                else if (!compared && !excluded)
                    yield return $"column {table}.{column} is neither compared nor excluded";
            }
        }
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static EdgeSnapshot[] ReadEdges(SqliteIndexStore store, string snapshotId)
    {
        var edges = store.GetEdges(snapshotId);
        SortEdges(edges);
        // GetEdges reads the edge rows. ReadEdgeSites compares the separate site rows.
        return edges.Select(edge => new EdgeSnapshot(
            edge.SourceSymbolId, edge.TargetSymbolId, edge.Kind, edge.Provenance, edge.ExtractorVersion,
            edge.SourceDocumentPath ?? "", edge.SourceStartLine, edge.SourceStartColumn,
            edge.SourceEndLine, edge.SourceEndColumn, edge.IsCrossGenerated, edge.TypeArgumentsJson,
            edge.ReceiverTypeConstraintsJson, edge.SourceIsBoundary, edge.TargetIsBoundary)).ToArray();
    }

    private static DiagnosticSnapshot[] ReadDiagnostics(SqliteIndexStore store, string snapshotId)
    {
        return store.GetDiagnostics(snapshotId)
            .Select(diagnostic => new DiagnosticSnapshot(
                diagnostic.ProjectName, NormalizeRedirectedDiagnosticPath(diagnostic.DocumentPath),
                diagnostic.Severity, diagnostic.Id, diagnostic.Message,
                diagnostic.StartLine, diagnostic.StartColumn, diagnostic.EndLine, diagnostic.EndColumn))
            .OrderBy(d => d.ProjectName, StringComparer.Ordinal)
            .ThenBy(d => d.DocumentPath ?? "", StringComparer.Ordinal)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.StartLine).ThenBy(d => d.StartColumn)
            .ThenBy(d => d.EndLine).ThenBy(d => d.EndColumn)
            .ThenBy(d => d.Severity, StringComparer.Ordinal)
            .ThenBy(d => d.Message, StringComparer.Ordinal)
            .ToArray();
    }

    private static (string SymbolId, string Kind, string Value, string? DocumentPath)[] ReadAnnotations(
        SqliteIndexStore store, string snapshotId)
    {
        var annotations = store.GetAnnotations(snapshotId);
        SortAnnotations(annotations);
        return annotations.Select(annotation => (annotation.SymbolId, annotation.Kind, annotation.Value, annotation.DocumentPath))
            .ToArray();
    }

    private static List<SnapshotMetadata> ReadSnapshotRow(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, payload_pruned, failure_reason_code, tool_version, database_schema_version,
                   output_schema_version, extractor_version, sdk_version, compiler_version, skipped_adapters,
                   analyzers_skipped
            FROM snapshots
            WHERE snapshot_id = @snapshotId;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return [];

        return [new SnapshotMetadata(
            reader.GetString(0), reader.GetInt32(1) != 0, reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetString(9), reader.GetInt32(10) != 0)];
    }

    public static List<DocumentSnapshot> ReadDocuments(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.relative_path, dv.content_hash, dv.is_generated, dv.line_starts, dv.encoding, dv.byte_count, dv.content
            FROM snapshot_documents sd
            JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
            JOIN documents d ON d.document_id = dv.document_id
            WHERE sd.snapshot_id = @snapshotId
            ORDER BY d.relative_path, dv.content_hash, dv.is_generated;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<DocumentSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new DocumentSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : Convert.ToHexStringLower(SHA256.HashData((byte[])reader.GetValue(6)))));

        return result;
    }

    private static List<SymbolIdentity> ReadSymbolIdentities(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ss.symbol_id, s.doc_comment_id, s.assembly_identity, s.kind
            FROM snapshot_symbols ss
            LEFT JOIN symbols s ON s.symbol_id = ss.symbol_id
            WHERE ss.snapshot_id = @snapshotId
            ORDER BY ss.symbol_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolIdentity>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SymbolIdentity(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));

        return result;
    }

    public static List<SymbolSnapshot> ReadSymbols(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, fqn, metadata_json, bare_fqn
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
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));

        return result;
    }

    public static List<DeclarationSnapshot> ReadDeclarations(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.symbol_id, docs.relative_path,
                   d.full_start, d.full_end,
                   d.signature_start, d.signature_end,
                   d.body_start, d.body_end,
                   d.name_start, d.name_end,
                   d.is_partial, d.is_generated, d.generator_identity
            FROM snapshot_declarations sdecl
            JOIN declarations d ON d.declaration_id = sdecl.declaration_id
            JOIN document_versions dv ON dv.document_version_id = d.document_version_id
            JOIN documents docs ON docs.document_id = dv.document_id
            WHERE sdecl.snapshot_id = @snapshotId
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

    public static List<EdgeSiteSnapshot> ReadEdgeSites(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.source_symbol_id, e.target_symbol_id, e.kind,
                   es.document_path, es.start_line, es.start_column, es.end_line, es.end_column,
                   es.provenance, es.extractor_version, es.is_cross_generated
            FROM edge_sites es
            JOIN edges e ON e.edge_id = es.edge_id
            WHERE e.snapshot_id = @snapshotId
            ORDER BY e.source_symbol_id, e.target_symbol_id, e.kind, es.provenance,
                     es.document_path, es.start_line, es.start_column, es.end_line, es.end_column;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<EdgeSiteSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new EdgeSiteSnapshot(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetBoolean(10)));
        return result;
    }

    public static List<string> ReadSnapshotGraphNodes(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id
            FROM snapshot_graph_nodes
            WHERE snapshot_id = @snapshotId
            ORDER BY node_id;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<(string Project, string Symbol)> ReadExcludedSymbols(SqliteIndexStore store, string snapshotId) =>
        store.LoadExcludedSymbolsByProject(snapshotId)
            .SelectMany(pair => pair.Value.Select(symbol => (Project: pair.Key, Symbol: symbol)))
            .OrderBy(pair => pair.Project, StringComparer.Ordinal)
            .ThenBy(pair => pair.Symbol, StringComparer.Ordinal).ToList();

    public static List<ProjectSnapshot> ReadProjects(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, assembly_name, target_framework, metadata_reference_identities,
                   compilation_options_fingerprint, document_paths_json, internals_visible_to, project_path, excluded_documents_fingerprint
            FROM projects
            WHERE snapshot_id = @snapshotId
            ORDER BY name, assembly_name, target_framework, metadata_reference_identities,
                     compilation_options_fingerprint, document_paths_json, internals_visible_to, project_path;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<ProjectSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new ProjectSnapshot(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        result.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.Name, b.Name);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.AssemblyName, b.AssemblyName);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TargetFramework, b.TargetFramework);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.MetadataReferenceIdentities, b.MetadataReferenceIdentities);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.CompilationOptionsFingerprint, b.CompilationOptionsFingerprint);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.DocumentPathsJson, b.DocumentPathsJson);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.InternalsVisibleToJson, b.InternalsVisibleToJson);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.ProjectPath, b.ProjectPath);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.ExcludedDocumentsFingerprint, b.ExcludedDocumentsFingerprint);
        });
        return result;
    }

    public static List<ProjectReferenceSnapshot> ReadProjectReferences(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.name, pr.referenced_project_name
            FROM project_references pr
            JOIN projects p ON p.project_id = pr.project_id
            WHERE p.snapshot_id = @snapshotId
            ORDER BY p.name, pr.referenced_project_name;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<ProjectReferenceSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new ProjectReferenceSnapshot(reader.GetString(0), reader.GetString(1)));
        result.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.ProjectName, b.ProjectName);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.ReferencedProjectName, b.ReferencedProjectName);
        });
        return result;
    }

    public static List<string> ReadTargetFrameworksForSymbol(string dbPath, string snapshotId, string docCommentId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT stf.tfm
            FROM symbol_target_frameworks stf
            JOIN symbols s ON s.symbol_id = stf.symbol_id
            WHERE stf.snapshot_id = @snapshotId AND s.doc_comment_id = @docCommentId
            ORDER BY stf.tfm;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@docCommentId", docCommentId);

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static List<SymbolTargetFrameworkSnapshot> ReadSymbolTargetFrameworks(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, tfm
            FROM symbol_target_frameworks
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id, tfm;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolTargetFrameworkSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SymbolTargetFrameworkSnapshot(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static List<SourceFtsSnapshot> ReadSourceFts(string dbPath, string snapshotId)
    {
        using var connection = OpenConnection(dbPath);

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
        using var connection = OpenConnection(dbPath);

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT symbol_id, fqn, doc_comment_id, kind, simple_name, name_tokens, container_tokens, kind_rank, is_test
            FROM symbol_fts
            WHERE snapshot_id = @snapshotId
            ORDER BY symbol_id, fqn, doc_comment_id, kind, simple_name, name_tokens, container_tokens, kind_rank, is_test;
            """;
        command.Parameters.AddWithValue("@snapshotId", snapshotId);

        var result = new List<SymbolFtsSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new SymbolFtsSnapshot(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetBoolean(8)));
        return result;
    }

    private static int? ReadNullableInt(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static void SortEdges(List<EdgeRecord> edges)
    {
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
            cmp = StringComparer.Ordinal.Compare(a.ExtractorVersion, b.ExtractorVersion);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.SourceDocumentPath ?? "", b.SourceDocumentPath ?? "");
            if (cmp != 0) return cmp;
            cmp = Nullable.Compare(a.SourceStartLine, b.SourceStartLine);
            if (cmp != 0) return cmp;
            cmp = Nullable.Compare(a.SourceStartColumn, b.SourceStartColumn);
            if (cmp != 0) return cmp;
            cmp = Nullable.Compare(a.SourceEndLine, b.SourceEndLine);
            if (cmp != 0) return cmp;
            cmp = Nullable.Compare(a.SourceEndColumn, b.SourceEndColumn);
            if (cmp != 0) return cmp;
            cmp = a.IsCrossGenerated.CompareTo(b.IsCrossGenerated);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.TypeArgumentsJson, b.TypeArgumentsJson);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.ReceiverTypeConstraintsJson, b.ReceiverTypeConstraintsJson);
            if (cmp != 0) return cmp;
            cmp = a.SourceIsBoundary.CompareTo(b.SourceIsBoundary);
            if (cmp != 0) return cmp;
            return a.TargetIsBoundary.CompareTo(b.TargetIsBoundary);
        });
    }

    // Cache roots can differ between index runs. Keep the solution key and the
    // complete intermediate path, so distinct generated files still differ.
    private static string? NormalizeRedirectedDiagnosticPath(string? path)
    {
        if (path == null)
            return null;

        var normalized = path.Replace('\\', '/');
        var match = Regex.Match(normalized, @"(?:^|/)lurp/([0-9a-f]{12})/obj/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? "@lurp-cache/" + normalized[(match.Index + (match.Value.StartsWith('/') ? 1 : 0))..] : path;
    }

    private static void SortAnnotations(List<AnnotationRecord> annotations)
    {
        annotations.Sort((a, b) =>
        {
            var cmp = StringComparer.Ordinal.Compare(a.SymbolId, b.SymbolId);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
            if (cmp != 0) return cmp;
            cmp = StringComparer.Ordinal.Compare(a.Value, b.Value);
            if (cmp != 0) return cmp;
            return StringComparer.Ordinal.Compare(a.DocumentPath, b.DocumentPath);
        });
    }

    private sealed record EdgeSnapshot(
        string SourceSymbolId, string TargetSymbolId, string Kind, string Provenance, string ExtractorVersion,
        string SourceDocumentPath, int? SourceStartLine, int? SourceStartColumn, int? SourceEndLine,
        int? SourceEndColumn, bool IsCrossGenerated, string? TypeArgumentsJson, string? ReceiverTypeConstraintsJson,
        bool SourceIsBoundary, bool TargetIsBoundary);

    private sealed record DiagnosticSnapshot(
        string ProjectName, string? DocumentPath, string Severity, string Id, string Message,
        int? StartLine, int? StartColumn, int? EndLine, int? EndColumn);

    private sealed record SnapshotMetadata(
        string Status, bool PayloadPruned, string? FailureReasonCode, string? ToolVersion, int DatabaseSchemaVersion,
        int OutputSchemaVersion, string? ExtractorVersion, string? SdkVersion, string? CompilerVersion, string SkippedAdapters,
        bool AnalyzersSkipped);

    public sealed record DocumentSnapshot(string RelativePath, string ContentHash, bool IsGenerated,
        string? LineStarts, string? Encoding, long? ByteCount, string? ContentSha256);

    private sealed record SymbolIdentity(string SymbolId, string? DocCommentId, string? AssemblyIdentity, string? Kind);

    public sealed record SymbolSnapshot(string SymbolId, string? FullyQualifiedName, string? MetadataJson, string? BareFqn);

    private sealed record SymbolTargetFrameworkSnapshot(string SymbolId, string Tfm);

    public sealed record DeclarationSnapshot(
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
        string Kind,
        string SimpleName,
        string NameTokens,
        string ContainerTokens,
        int KindRank,
        bool IsTest);

    public sealed record EdgeSiteSnapshot(
        string SourceSymbolId,
        string TargetSymbolId,
        string Kind,
        string DocumentPath,
        int StartLine,
        int StartColumn,
        int EndLine,
        int EndColumn,
        string Provenance,
        string ExtractorVersion,
        bool IsCrossGenerated);

    public sealed record ProjectSnapshot(
        string Name,
        string? AssemblyName,
        string? TargetFramework,
        string? MetadataReferenceIdentities,
        string? CompilationOptionsFingerprint,
        string? DocumentPathsJson,
        string? InternalsVisibleToJson,
        string? ProjectPath,
        string? ExcludedDocumentsFingerprint);

    public sealed record ProjectReferenceSnapshot(string ProjectName, string ReferencedProjectName);
}
