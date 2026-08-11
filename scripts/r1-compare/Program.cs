using Lurp.Storage;
using Microsoft.Data.Sqlite;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: r1-compare <db-path-b> <db-path-c>");
    Console.Error.WriteLine("  Compares latest snapshots in two databases field-by-field.");
    Environment.Exit(2);
}

var dbPathB = args[0];
var dbPathC = args[1];

if (!File.Exists(dbPathB)) { Console.Error.WriteLine($"File not found: {dbPathB}"); Environment.Exit(1); }
if (!File.Exists(dbPathC)) { Console.Error.WriteLine($"File not found: {dbPathC}"); Environment.Exit(1); }

using var storeB = OpenStore(dbPathB);
using var storeC = OpenStore(dbPathC);

var snapshotB = storeB.LoadLatestSnapshot()?.SnapshotId
    ?? throw new InvalidOperationException($"No snapshot in {dbPathB}");
var snapshotC = storeC.LoadLatestSnapshot()?.SnapshotId
    ?? throw new InvalidOperationException($"No snapshot in {dbPathC}");

Console.WriteLine($"B snapshot: {snapshotB}");
Console.WriteLine($"C snapshot: {snapshotC}");

var errors = new List<string>();

// --- Snapshot ID equality ---
if (snapshotB != snapshotC)
{
    errors.Add($"Snapshot IDs differ: B={snapshotB} C={snapshotC}");
}

// --- Symbol IDs ---
var symbolsB = storeB.GetSymbolIdsInSnapshot(snapshotB);
var symbolsC = storeC.GetSymbolIdsInSnapshot(snapshotC);
symbolsB.Sort(StringComparer.Ordinal);
symbolsC.Sort(StringComparer.Ordinal);

if (symbolsB.Count != symbolsC.Count)
{
    var onlyB = symbolsB.Except(symbolsC, StringComparer.Ordinal).Take(10).ToList();
    var onlyC = symbolsC.Except(symbolsB, StringComparer.Ordinal).Take(10).ToList();
    errors.Add($"Symbol count mismatch: B={symbolsB.Count} C={symbolsC.Count}");
    if (onlyB.Count > 0) errors.Add($"  Only in B: {string.Join(", ", onlyB)}");
    if (onlyC.Count > 0) errors.Add($"  Only in C: {string.Join(", ", onlyC)}");
}
else if (!symbolsB.SequenceEqual(symbolsC, StringComparer.Ordinal))
{
    errors.Add("Symbol ID sets differ (same count, different IDs).");
}

// --- Symbols (FQN + metadata_json) ---
var symRowsB = ReadSymbols(dbPathB, snapshotB);
var symRowsC = ReadSymbols(dbPathC, snapshotC);
if (!symRowsB.SequenceEqual(symRowsC))
{
    errors.Add($"Symbol metadata mismatch ({symRowsB.Count} vs {symRowsC.Count} rows).");
}

// --- Declarations ---
var declB = ReadDeclarations(dbPathB, snapshotB);
var declC = ReadDeclarations(dbPathC, snapshotC);
if (declB.Count != declC.Count)
{
    errors.Add($"Declaration count mismatch: B={declB.Count} C={declC.Count}");
}
else if (!declB.SequenceEqual(declC))
{
    errors.Add("Declaration data mismatch (same count, different values).");
}

// --- Edges ---
var edgesB = storeB.GetEdges(snapshotB);
var edgesC = storeC.GetEdges(snapshotC);
NormalizeEdges(edgesB);
NormalizeEdges(edgesC);

if (edgesB.Count != edgesC.Count)
{
    var bSet = edgesB.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
    var cSet = edgesC.Select(e => $"{e.SourceSymbolId}|{e.TargetSymbolId}|{e.Kind}|{e.Provenance}").ToHashSet();
    var onlyB = bSet.Except(cSet).Take(10).ToList();
    var onlyC = cSet.Except(bSet).Take(10).ToList();
    errors.Add($"Edge count mismatch: B={edgesB.Count} C={edgesC.Count}");
    if (onlyB.Count > 0) errors.Add($"  Only in B: {string.Join("; ", onlyB)}");
    if (onlyC.Count > 0) errors.Add($"  Only in C: {string.Join("; ", onlyC)}");
}
else
{
    for (int i = 0; i < edgesB.Count; i++)
    {
        var diff = EdgeDiff(edgesB[i], edgesC[i]);
        if (diff is not null)
            errors.Add($"Edge[{i}] mismatch: {diff}");
    }
}

// --- Diagnostics ---
var diagB = storeB.GetDiagnostics(snapshotB);
var diagC = storeC.GetDiagnostics(snapshotC);
NormalizeDiagnostics(diagB);
NormalizeDiagnostics(diagC);
if (diagB.Count != diagC.Count)
    errors.Add($"Diagnostic count mismatch: B={diagB.Count} C={diagC.Count}");
else
    for (int i = 0; i < diagB.Count; i++)
        if (!DiagEqual(diagB[i], diagC[i]))
            errors.Add($"Diagnostic[{i}] mismatch.");

// --- Binding incompleteness ---
var incB = NormalizeBindingIncompleteness(storeB.GetBindingIncompleteness(snapshotB));
var incC = NormalizeBindingIncompleteness(storeC.GetBindingIncompleteness(snapshotC));
if (!incB.SequenceEqual(incC))
    errors.Add("Binding incompleteness mismatch.");

// --- Annotations ---
var annB = storeB.GetAnnotations(snapshotB);
var annC = storeC.GetAnnotations(snapshotC);
NormalizeAnnotations(annB);
NormalizeAnnotations(annC);
if (annB.Count != annC.Count)
    errors.Add($"Annotation count mismatch: B={annB.Count} C={annC.Count}");

// --- FTS ---
var ftsSourceB = ReadSourceFts(dbPathB, snapshotB);
var ftsSourceC = ReadSourceFts(dbPathC, snapshotC);
if (!ftsSourceB.SequenceEqual(ftsSourceC))
    errors.Add("Source FTS mismatch.");

var ftsSymB = ReadSymbolFts(dbPathB, snapshotB);
var ftsSymC = ReadSymbolFts(dbPathC, snapshotC);
if (!ftsSymB.SequenceEqual(ftsSymC))
    errors.Add("Symbol FTS mismatch.");

// --- Result ---
if (errors.Count == 0)
{
    Console.WriteLine($"PASS: B5 ≡ C ({edgesB.Count} edges, {symbolsB.Count} symbols, 0 diffs)");
    Environment.Exit(0);
}
else
{
    Console.Error.WriteLine($"FAIL: {errors.Count} difference(s):");
    foreach (var e in errors)
        Console.Error.WriteLine($"  - {e}");
    Environment.Exit(1);
}

// ========== helpers ==========

static SqliteIndexStore OpenStore(string dbPath)
{
    var store = new SqliteIndexStore(dbPath);
    store.Open();
    return store;
}

static List<(string SymbolId, string? Fqn, string? MetadataJson)> ReadSymbols(string dbPath, string snapshotId)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT symbol_id, fqn, metadata_json FROM snapshot_symbols WHERE snapshot_id = @id ORDER BY symbol_id;";
    cmd.Parameters.AddWithValue("@id", snapshotId);
    var result = new List<(string, string?, string?)>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        result.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
    return result;
}

static List<(string SymbolId, string DocPath, int? FullStart, int? FullEnd, int? SigStart, int? SigEnd,
    int? BodyStart, int? BodyEnd, int? NameStart, int? NameEnd, bool IsPartial, bool IsGenerated, string? GenId)>
    ReadDeclarations(string dbPath, string snapshotId)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        SELECT d.symbol_id, docs.relative_path,
               d.full_start, d.full_end, d.signature_start, d.signature_end,
               d.body_start, d.body_end, d.name_start, d.name_end,
               d.is_partial, d.is_generated, d.generator_identity
        FROM snapshot_documents sd
        JOIN document_versions dv ON dv.document_version_id = sd.document_version_id
        JOIN documents docs ON docs.document_id = dv.document_id
        JOIN declarations d ON d.document_version_id = sd.document_version_id
        WHERE sd.snapshot_id = @id
        ORDER BY d.symbol_id, docs.relative_path, d.full_start, d.full_end,
                 d.signature_start, d.signature_end, d.body_start, d.body_end,
                 d.name_start, d.name_end;";
    cmd.Parameters.AddWithValue("@id", snapshotId);
    var result = new List<(string, string, int?, int?, int?, int?, int?, int?, int?, int?, bool, bool, string?)>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        result.Add((
            r.GetString(0), r.GetString(1),
            ReadNI(r, 2), ReadNI(r, 3), ReadNI(r, 4), ReadNI(r, 5),
            ReadNI(r, 6), ReadNI(r, 7), ReadNI(r, 8), ReadNI(r, 9),
            r.GetInt32(10) != 0, r.GetInt32(11) != 0,
            r.IsDBNull(12) ? null : r.GetString(12)));
    return result;
}

static int? ReadNI(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

static void NormalizeEdges(List<EdgeRecord> edges)
{
    var field = typeof(EdgeRecord).GetField("<SnapshotId>k__BackingField",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    foreach (var e in edges)
        field?.SetValue(e, string.Empty);

    edges.Sort((a, b) =>
    {
        int c = StringComparer.Ordinal.Compare(a.SourceSymbolId, b.SourceSymbolId);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.TargetSymbolId, b.TargetSymbolId);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Kind, b.Kind);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Provenance, b.Provenance);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.ExtractorVersion, b.ExtractorVersion);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.SourceDocumentPath ?? "", b.SourceDocumentPath ?? "");
        if (c != 0) return c;
        c = (a.SourceStartLine ?? 0).CompareTo(b.SourceStartLine ?? 0);
        if (c != 0) return c;
        c = (a.SourceStartColumn ?? 0).CompareTo(b.SourceStartColumn ?? 0);
        if (c != 0) return c;
        c = (a.SourceEndLine ?? 0).CompareTo(b.SourceEndLine ?? 0);
        if (c != 0) return c;
        c = (a.SourceEndColumn ?? 0).CompareTo(b.SourceEndColumn ?? 0);
        if (c != 0) return c;
        c = a.IsCrossGenerated.CompareTo(b.IsCrossGenerated);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.TypeArgumentsJson ?? "", b.TypeArgumentsJson ?? "");
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.ReceiverTypeConstraintsJson ?? "", b.ReceiverTypeConstraintsJson ?? "");
        if (c != 0) return c;
        c = (a.SourceNodeKind?.ToString() ?? "").CompareTo(b.SourceNodeKind?.ToString() ?? "");
        if (c != 0) return c;
        return (a.TargetNodeKind?.ToString() ?? "").CompareTo(b.TargetNodeKind?.ToString() ?? "");
    });
}

static string? EdgeDiff(EdgeRecord a, EdgeRecord b)
{
    if (a.SourceSymbolId != b.SourceSymbolId) return $"SourceSymbolId: {a.SourceSymbolId} != {b.SourceSymbolId}";
    if (a.TargetSymbolId != b.TargetSymbolId) return $"TargetSymbolId: {a.TargetSymbolId} != {b.TargetSymbolId}";
    if (a.Kind != b.Kind) return $"Kind: {a.Kind} != {b.Kind}";
    if (a.Provenance != b.Provenance) return $"Provenance: {a.Provenance} != {b.Provenance}";
    if (a.ExtractorVersion != b.ExtractorVersion) return $"ExtractorVersion: {a.ExtractorVersion} != {b.ExtractorVersion}";
    if (a.SourceDocumentPath != b.SourceDocumentPath) return $"SourceDocumentPath: {a.SourceDocumentPath} != {b.SourceDocumentPath}";
    if (a.SourceStartLine != b.SourceStartLine) return $"SourceStartLine: {a.SourceStartLine} != {b.SourceStartLine}";
    if (a.SourceEndLine != b.SourceEndLine) return $"SourceEndLine: {a.SourceEndLine} != {b.SourceEndLine}";
    if (a.SourceStartColumn != b.SourceStartColumn) return $"SourceStartColumn: {a.SourceStartColumn} != {b.SourceStartColumn}";
    if (a.SourceEndColumn != b.SourceEndColumn) return $"SourceEndColumn: {a.SourceEndColumn} != {b.SourceEndColumn}";
    if (a.ReceiverTypeConstraintsJson != b.ReceiverTypeConstraintsJson) return "ReceiverTypeConstraintsJson";
    if (a.IsCrossGenerated != b.IsCrossGenerated) return "IsCrossGenerated";
    if (a.TypeArgumentsJson != b.TypeArgumentsJson) return "TypeArgumentsJson";
    if (a.SourceNodeKind != b.SourceNodeKind) return "SourceNodeKind";
    if (a.TargetNodeKind != b.TargetNodeKind) return "TargetNodeKind";
    return null;
}

static void NormalizeDiagnostics(List<DiagnosticRecord> diags)
{
    diags.Sort((a, b) =>
    {
        int c = StringComparer.Ordinal.Compare(a.DocumentPath ?? "", b.DocumentPath ?? "");
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Id, b.Id);
        if (c != 0) return c;
        c = (a.StartLine ?? 0).CompareTo(b.StartLine ?? 0);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Message, b.Message);
        if (c != 0) return c;
        return StringComparer.Ordinal.Compare(a.ProjectName, b.ProjectName);
    });
}

static bool DiagEqual(DiagnosticRecord a, DiagnosticRecord b) =>
    a.ProjectName == b.ProjectName && a.DocumentPath == b.DocumentPath &&
    a.Severity == b.Severity && a.Id == b.Id && a.Message == b.Message &&
    a.StartLine == b.StartLine && a.StartColumn == b.StartColumn &&
    a.EndLine == b.EndLine && a.EndColumn == b.EndColumn;

static void NormalizeAnnotations(List<AnnotationRecord> anns)
{
    anns.Sort((a, b) =>
    {
        int c = StringComparer.Ordinal.Compare(a.SymbolId, b.SymbolId);
        if (c != 0) return c;
        return StringComparer.Ordinal.Compare(a.Kind, b.Kind);
    });
}

static List<BindingIncompletenessRecord> NormalizeBindingIncompleteness(List<BindingIncompletenessRecord> records)
{
    records.Sort((a, b) =>
    {
        int c = StringComparer.Ordinal.Compare(a.ProjectName, b.ProjectName);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.DocumentPath ?? "", b.DocumentPath ?? "");
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.Reason, b.Reason);
        if (c != 0) return c;
        return a.Count.CompareTo(b.Count);
    });
    return records;
}

static List<(string DocPath, string Content)> ReadSourceFts(string dbPath, string snapshotId)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT document_path, content FROM source_fts WHERE snapshot_id = @id ORDER BY document_path, content;";
    cmd.Parameters.AddWithValue("@id", snapshotId);
    var r = new List<(string, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) r.Add((reader.GetString(0), reader.GetString(1)));
    return r;
}

static List<(string SymbolId, string Fqn, string DocCommentId, string Kind)> ReadSymbolFts(string dbPath, string snapshotId)
{
    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT symbol_id, fqn, doc_comment_id, kind FROM symbol_fts WHERE snapshot_id = @id ORDER BY symbol_id, fqn, doc_comment_id, kind;";
    cmd.Parameters.AddWithValue("@id", snapshotId);
    var r = new List<(string, string, string, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read()) r.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
    return r;
}
