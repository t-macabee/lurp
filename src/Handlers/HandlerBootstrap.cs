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
    /// The single error idiom for handlers: raises a diagnosed refusal carrying
    /// <paramref name="code"/> as the process exit code. Marked <c>[DoesNotReturn]</c> so
    /// callers keep the definite-assignment and nullability behaviour they had when this
    /// called <see cref="Environment.Exit(int)"/> inline.
    /// <para>
    /// It throws rather than exiting so a long-lived host cannot be killed by one bad
    /// request. <c>Program</c> catches <see cref="HandlerFailureException"/> and reproduces
    /// the previous CLI behaviour exactly: the message on stderr, then that exit code.
    /// </para>
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Fail(string message, int code = 1)
        => throw new HandlerFailureException(message, code);

    private static readonly AsyncLocal<TextWriter?> PayloadSink = new();

    /// <summary>
    /// Where handler payloads are written. Defaults to <see cref="Console.Out"/>, which is
    /// what the CLI wants and what every existing consumer sees.
    /// <para>
    /// A host that speaks a protocol over stdout (MCP's stdio transport owns it) cannot
    /// allow a stray payload write to corrupt its stream, so it redirects this instead of
    /// the process-wide <see cref="Console"/>. Backed by <see cref="AsyncLocal{T}"/> so
    /// concurrent requests each capture their own output rather than racing over one
    /// global writer. Diagnostics stay on stderr and are deliberately not routed here.
    /// </para>
    /// </summary>
    public static TextWriter Out
    {
        get => PayloadSink.Value ?? Console.Out;
        set => PayloadSink.Value = value;
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
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Fail("ERROR: --output-dir=path or LURP_OUTPUT_DIR is required (INDEXER_OUTPUT_DIR is accepted for back-compat).");
        }

        return outputDirArg;
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

    private static readonly AsyncLocal<string?> SessionSnapshot = new();

    /// <summary>
    /// Pins every unqualified snapshot resolution in this async flow to one snapshot.
    /// Null (the default, and the CLI's behaviour) means "resolve latest per call".
    /// <para>
    /// A one-shot CLI process cannot observe the difference. A long-lived host can:
    /// re-indexing mid-session would otherwise move the ground under a caller partway
    /// through reasoning about a symbol, with early and late answers describing
    /// different snapshots and nothing marking the switch. A host pins at session start
    /// and advances deliberately.
    /// </para>
    /// </summary>
    public static string? PinnedSnapshotId
    {
        get => SessionSnapshot.Value;
        set => SessionSnapshot.Value = value;
    }

    /// <summary>
    /// Precedence: an explicit <c>--snapshot=</c> wins, then the session pin, then latest.
    /// An explicit request always beats the pin, so a host can still ask about a specific
    /// snapshot without unpinning.
    /// </summary>
    public static string ResolveSnapshotId(SqliteIndexStore store, string? snapshotArg)
    {
        if (!string.IsNullOrEmpty(snapshotArg))
            return snapshotArg;

        if (!string.IsNullOrEmpty(PinnedSnapshotId))
            return PinnedSnapshotId;

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
        {
            Fail("ERROR: No snapshots found in the database.");
        }

        return snapshotId;
    }
}
