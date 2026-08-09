using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

/// <summary>
/// How a read handler renders its payload. <see cref="Json"/> is the historical
/// default and stays the default everywhere, so no existing consumer changes shape
/// unless it asks to.
/// </summary>
internal enum OutputMode
{
    /// <summary>Human/agent-readable digest : counts, names, and the continuation token.</summary>
    Summary,

    /// <summary>One indented JSON document (the historical output).</summary>
    Json,

    /// <summary>
    /// Newline-delimited JSON: a leading <c>{"type":"meta",...}</c> envelope followed by
    /// one compact object per result, so a consumer can stream and stop early.
    /// </summary>
    Jsonl,
}

internal static class HandlerBootstrap
{
    /// <summary>
    /// The single error+exit idiom for handlers: throws
    /// <see cref="CliExitException"/> carrying <paramref name="message"/> and
    /// <paramref name="code"/>; <c>Program.Main</c> writes the message to stderr
    /// and terminates with the code. Marked <c>[DoesNotReturn]</c> so callers
    /// keep the definite-assignment and nullability behaviour they had when calling
    /// <see cref="Environment.Exit(int)"/> inline.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Fail(string message, int code = 1)
    {
        throw new CliExitException(message, code);
    }

    public static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }

    /// <summary>
    /// Document paths are persisted with forward slashes (see <c>Identity</c>), so a
    /// caller on Windows who pastes a native path from the shell would otherwise miss
    /// every stored document. Normalize the CLI form to the stored form.
    /// </summary>
    public static string? NormalizeDocumentPath(string? path)
        => path?.Replace('\\', '/');

    public static readonly System.Text.Json.JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    public static readonly System.Text.Json.JsonSerializerOptions CompactJson = new() { WriteIndented = false };

    /// <summary>
    /// Parses <c>--output=summary|json|jsonl</c>. <paramref name="allowJsonl"/> is false for
    /// surfaces whose payload is a single document rather than a sequence (the context
    /// capsule): claiming to stream one object per line there would be a lie about the
    /// shape, so it is rejected instead of silently degraded to <see cref="OutputMode.Json"/>.
    /// </summary>
    public static OutputMode ParseOutputMode(string[] args, bool allowJsonl = true)
    {
        var raw = GetArgValue(args, "--output=");
        if (string.IsNullOrEmpty(raw))
            return OutputMode.Json;

        switch (raw.ToLowerInvariant())
        {
            case "summary":
                return OutputMode.Summary;
            case "json":
                return OutputMode.Json;
            case "jsonl" when allowJsonl:
                return OutputMode.Jsonl;
            case "jsonl":
                Fail("ERROR: --output=jsonl is not supported for this mode; its payload is a single document. Use --output=json or --output=summary.");
                return OutputMode.Json;
            default:
                Fail($"ERROR: --output must be one of: summary, json{(allowJsonl ? ", jsonl" : "")}.");
                return OutputMode.Json;
        }
    }

    /// <summary>
    /// <c>--quiet</c> suppresses everything that is not the payload : the freshness
    /// stderr line here, and additionally the stdout echo of an artifact that was also
    /// written to a file (see <c>--mode=context</c>).
    /// </summary>
    public static bool IsQuiet(string[] args) => args.Contains("--quiet");

    public static int ParsePositiveIntArg(string[] args, string prefix, int defaultValue)
    {
        var raw = GetArgValue(args, prefix);
        if (string.IsNullOrEmpty(raw))
            return defaultValue;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            Fail($"ERROR: {prefix.TrimEnd('=')} must be a positive integer.");
        }

        return value;
    }

    /// <summary>
    /// Decodes and validates a <see cref="SequenceCursor"/>, exiting 1 with an explicit
    /// message rather than resuming an offset against a sequence it was not issued for.
    /// </summary>
    public static SequenceCursor? ResolveSequenceCursor(string[] args, string snapshotId, string fingerprint, string kind)
    {
        var raw = GetArgValue(args, "--cursor=");
        if (string.IsNullOrEmpty(raw))
            return null;

        var cursor = SequenceCursor.TryDecode(raw);
        if (cursor == null)
        {
            Fail("ERROR: --cursor is not a valid continuation token.");
            return null;
        }

        try
        {
            cursor.Validate(snapshotId, fingerprint, kind);
        }
        catch (ArgumentException ex)
        {
            Fail($"ERROR: {ex.Message}");
            return null;
        }

        return cursor;
    }

    public static FreshnessMode ParseFreshnessMode(string[] args)
    {
        var raw = GetArgValue(args, "--freshness=");
        return raw?.ToLowerInvariant() switch
        {
            "hash" => FreshnessMode.Hash,
            "off" => FreshnessMode.Off,
            _ => FreshnessMode.Auto,
        };
    }

    public static FreshnessStamp ComputeFreshnessStamp(ISnapshotManifestStore manifests, ISnapshotDocumentStore documents, string snapshotId, string[] args)
        => WorkspaceFreshness.CheckFreshnessCheap(manifests, documents, snapshotId, ParseFreshnessMode(args));

    public static object FreshnessJson(FreshnessStamp stamp) => new
    {
        state = stamp.State,
        method = stamp.Method,
        changed_document_count = stamp.ChangedDocumentCount,
        changed_documents_sample = stamp.ChangedDocumentsSample,
        checked_at_utc = stamp.CheckedAtUtc,
        snapshot_id = stamp.SnapshotId,
    };

    public static void EnforceRequireFresh(string[] args, FreshnessStamp stamp)
    {
        if (!args.Contains("--require-fresh"))
            return;

        if (stamp.State != "fresh")
        {
            Fail($"ERROR: snapshot '{stamp.SnapshotId}' is not fresh (state={stamp.State}, method={stamp.Method}, changedDocuments={stamp.ChangedDocumentCount}). Re-index, or drop --require-fresh to read it anyway.", 2);
        }
    }

    public static void PrintFreshnessLine(string[] args, FreshnessStamp stamp)
    {
        if (IsQuiet(args))
            return;

        Console.Error.WriteLine($"freshness: state={stamp.State} method={stamp.Method} changedDocuments={stamp.ChangedDocumentCount} snapshot={stamp.SnapshotId}");
    }

    public static string RequireArg(string[] args, string prefix, params string[] errorLines)
    {
        var value = GetArgValue(args, prefix);
        if (string.IsNullOrEmpty(value))
            Fail(string.Join(Environment.NewLine, errorLines));

        return value;
    }

    public static string ResolveOutputDir(string[] args)
    {
        var outputDirArg = GetArgValue(args, "--output-dir=")
            ?? Environment.GetEnvironmentVariable("LURP_OUTPUT_DIR")
            ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (!string.IsNullOrEmpty(outputDirArg))
            return outputDirArg;

        var solutionArg = GetArgValue(args, "--solution=")
            ?? Environment.GetEnvironmentVariable("LURP_SOLUTION_PATH")
            ?? Environment.GetEnvironmentVariable("INDEXER_SOLUTION_PATH");
        if (!string.IsNullOrEmpty(solutionArg))
        {
            var derived = Path.GetDirectoryName(Path.GetFullPath(solutionArg));
            if (!string.IsNullOrEmpty(derived))
                return derived;
        }

        Fail("ERROR: --output-dir=path, LURP_OUTPUT_DIR, or --solution=path is required (INDEXER_OUTPUT_DIR and INDEXER_SOLUTION_PATH are accepted for back-compat).");
        return string.Empty;
    }

    public static string ResolveDbPath(string outputDir)
    {
        var dbPath = Path.Combine(Path.GetFullPath(outputDir), "index.db");
        if (!File.Exists(dbPath))
        {
            Fail("ERROR: Index database not found at " + dbPath);
        }

        return dbPath;
    }

    public static SqliteIndexStore OpenStore(string dbPath)
    {
        var store = new SqliteIndexStore(dbPath);
        store.Open();
        return store;
    }

    public static string ResolveSnapshotId(SqliteIndexStore store, string? snapshotArg)
    {
        if (!string.IsNullOrEmpty(snapshotArg))
            return snapshotArg;

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
        {
            Fail("ERROR: No snapshots found in the database.");
        }

        return snapshotId;
    }

    /// <summary>
    /// Opens the store for <paramref name="args"/>, resolves the snapshot id from
    /// <paramref name="snapshotArg"/> (or the latest snapshot when null), calls
    /// <paramref name="body"/>, and closes the store in a <c>finally</c>.
    /// </summary>
    public static T WithStore<T>(string[] args, string? snapshotArg, Func<SqliteIndexStore, string, T> body)
    {
        var outputDir = ResolveOutputDir(args);
        var dbPath = ResolveDbPath(outputDir);
        var store = OpenStore(dbPath);
        try
        {
            var snapshotId = ResolveSnapshotId(store, snapshotArg);
            return body(store, snapshotId);
        }
        finally
        {
            store.Close();
        }
    }

    /// <summary>
    /// Computes freshness, enforces <c>--require-fresh</c>, and prints the freshness
    /// line (unless <c>--quiet</c>). Returns the stamp so callers can embed it in
    /// the payload.
    /// </summary>
    public static FreshnessStamp ResolveFreshness(string[] args, SqliteIndexStore store, string snapshotId)
    {
        var stamp = ComputeFreshnessStamp(store, store, snapshotId, args);
        EnforceRequireFresh(args, stamp);
        PrintFreshnessLine(args, stamp);
        return stamp;
    }

    /// <summary>
    /// Resolves a symbol identifier argument to the canonical <c>docCommentId|assemblyIdentity</c>
    /// form. Accepts the pipe-separated form directly, a bare doc-comment ID (e.g. T:Some.Type),
    /// or a fully-qualified name (e.g. Some.Namespace.Type). This eliminates the intermediate
    /// <c>find-symbol</c> step that was previously required to obtain the resolvable form.
    /// </summary>
    public static string ResolveSymbolArg(ISearchStore store, string symbolArg, string snapshotId, bool includeGenerated = false)
    {
        // The canonical form is trusted as-is; validating it is the caller's choice
        // (existing behavior for all five call sites).
        if (symbolArg.Contains('|'))
            return symbolArg;

        var info = ResolveSymbolInfo(store, symbolArg, snapshotId, includeGenerated);
        if (info != null)
            return info.SymbolId.Value;

        Fail(
            $"ERROR: Could not resolve '{symbolArg}' to a known symbol in snapshot '{snapshotId}'. " +
            "Pass the full 'docCommentId|assemblyIdentity' symbol ID, a doc-comment ID (e.g. T:Some.Type), " +
            "or a fully-qualified name (e.g. Some.Namespace.Type).");
        return symbolArg; // unreachable
    }

    /// <summary>
    /// Resolves any accepted symbol identifier form to its indexed record, or null when no
    /// symbol matches. Accepts the full <c>docCommentId|assemblyIdentity</c> symbol ID
    /// (validated via its doc-comment part), a bare doc-comment ID (e.g. <c>T:Some.Type</c>),
    /// or a fully-qualified name (e.g. <c>Some.Namespace.Type</c>).
    /// </summary>
    public static IndexedSymbolInfo? ResolveSymbolInfo(ISearchStore store, string symbolArg, string snapshotId, bool includeGenerated = false)
    {
        var candidate = symbolArg;
        var pipe = symbolArg.IndexOf('|');
        if (pipe > 0)
            candidate = symbolArg[..pipe];

        // Doc-comment ID format: starts with a Roslyn prefix character followed by ':'
        // (T: for types, M: for methods, P: for properties, E: for events, F: for fields, N: for namespaces)
        if (candidate.Length >= 2 && candidate[1] == ':' && "TMPEFN".Contains(candidate[0]))
        {
            var byId = store.ResolveSymbolByDocCommentId(candidate, snapshotId, includeGenerated);
            if (byId != null)
                return byId;
        }

        // The full ID form carries no FQN to fall back to.
        if (pipe > 0)
            return null;

        return store.ResolveSymbolByFqn(symbolArg, snapshotId, includeGenerated);
    }
}
