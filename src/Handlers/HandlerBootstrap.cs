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
    /// <summary>Human/agent-readable digest — counts, names, and the continuation token.</summary>
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
    public static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }

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
                Console.Error.WriteLine("ERROR: --output=jsonl is not supported for this mode; its payload is a single document. Use --output=json or --output=summary.");
                Environment.Exit(1);
                return OutputMode.Json;
            default:
                Console.Error.WriteLine($"ERROR: --output must be one of: summary, json{(allowJsonl ? ", jsonl" : "")}.");
                Environment.Exit(1);
                return OutputMode.Json;
        }
    }

    /// <summary>
    /// <c>--quiet</c> suppresses everything that is not the payload — the freshness
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
            Console.Error.WriteLine($"ERROR: {prefix.TrimEnd('=')} must be a positive integer.");
            Environment.Exit(1);
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
            Console.Error.WriteLine("ERROR: --cursor is not a valid continuation token.");
            Environment.Exit(1);
            return null;
        }

        try
        {
            cursor.Validate(snapshotId, fingerprint, kind);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.Exit(1);
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

    public static FreshnessStamp ComputeFreshnessStamp(ISnapshotStore store, string snapshotId, string[] args)
        => WorkspaceFreshness.CheckFreshnessCheap(store, snapshotId, ParseFreshnessMode(args));

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
            Console.Error.WriteLine($"ERROR: snapshot '{stamp.SnapshotId}' is not fresh (state={stamp.State}, method={stamp.Method}, changedDocuments={stamp.ChangedDocumentCount}). Re-index, or drop --require-fresh to read it anyway.");
            Environment.Exit(2);
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
        {
            foreach (var line in errorLines)
                Console.Error.WriteLine(line);
            Environment.Exit(1);
        }

        return value;
    }

    public static string ResolveOutputDir(string[] args)
    {
        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        return outputDirArg;
    }

    public static string ResolveDbPath(string outputDir)
    {
        var dbPath = Path.Combine(Path.GetFullPath(outputDir), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
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
            Console.Error.WriteLine("ERROR: No snapshots found in the database.");
            Environment.Exit(1);
        }

        return snapshotId;
    }
}
