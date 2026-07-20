using System.Globalization;
using System.Text.Json;
using Lurp.Storage;

namespace Lurp.Handlers;

internal static class GetSymbolHandler
{
    private sealed record ViewSelection(ViewKind ViewKind, bool IsMetadataView, bool IsContainingType, bool IsSurrounding, int ContextLines);

    public static void Run(string[] args)
    {
        var symbolArg = RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=get-symbol.");
        var viewArg = RequireArg(args, "--view=", "ERROR: --view=<view-kind> is required for --mode=get-symbol.",
            "  Valid values: metadata, signature, body, declaration, containing-type, surrounding");

        var outputDirArg = GetArgValue(args, "--output-dir=") ?? Environment.GetEnvironmentVariable("INDEXER_OUTPUT_DIR");
        if (string.IsNullOrEmpty(outputDirArg))
        {
            Console.Error.WriteLine("ERROR: --output-dir=path or INDEXER_OUTPUT_DIR is required.");
            Environment.Exit(1);
        }

        var snapshotArg = GetArgValue(args, "--snapshot=");
        var contextLinesArg = GetArgValue(args, "--context-lines=");
        var includeGenerated = args.Contains("--include-generated");

        var dbPath = Path.Combine(Path.GetFullPath(outputDirArg!), "index.db");
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine("ERROR: Index database not found at " + dbPath);
            Environment.Exit(1);
        }

        var store = new SqliteIndexStore(dbPath);
        store.Open(dbPath);

        try
        {
            var snapshotId = ResolveSnapshotId(store, snapshotArg);
            var view = ResolveViewSelection(viewArg!, contextLinesArg);
            WriteRequestedView(store, view, symbolArg!, snapshotId, viewArg!, includeGenerated);
        }
        finally
        {
            store.Close();
        }
    }

    private static string ResolveSnapshotId(SqliteIndexStore store, string? snapshotArg)
    {
        if (!string.IsNullOrEmpty(snapshotArg))
            return snapshotArg;

        var snapshotId = store.GetLatestSnapshotId();
        if (snapshotId == null)
        {
            Console.Error.WriteLine("ERROR: No snapshots found in the database.");
            Environment.Exit(1);
        }

        return snapshotId!;
    }

    private static ViewSelection ResolveViewSelection(string viewArg, string? contextLinesArg)
    {
        switch (viewArg.ToLowerInvariant())
        {
            case "metadata":
                return new ViewSelection(ViewKind.Declaration, IsMetadataView: true, IsContainingType: false, IsSurrounding: false, ContextLines: 3);
            case "signature":
                return new ViewSelection(ViewKind.Signature, false, false, false, 3);
            case "body":
                return new ViewSelection(ViewKind.Body, false, false, false, 3);
            case "declaration":
                return new ViewSelection(ViewKind.Declaration, false, false, false, 3);
            case "containing-type":
                return new ViewSelection(ViewKind.Declaration, false, true, false, 3);
            case "surrounding":
                var contextLines = 3;
                if (!string.IsNullOrEmpty(contextLinesArg) && int.TryParse(contextLinesArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    contextLines = parsed;
                return new ViewSelection(ViewKind.Declaration, false, false, true, contextLines);
            default:
                Console.Error.WriteLine($"ERROR: Unknown view kind '{viewArg}'.");
                Console.Error.WriteLine("  Valid values: metadata, signature, body, declaration, containing-type, surrounding");
                Environment.Exit(1);
                return new ViewSelection(ViewKind.Declaration, false, false, false, 3);
        }
    }

    private static void WriteRequestedView(SqliteIndexStore store, ViewSelection view, string symbolArg, string snapshotId, string viewArg, bool includeGenerated)
    {
        if (view.IsMetadataView)
            WriteMetadataView(store, symbolArg, snapshotId);
        else if (view.IsContainingType)
            WriteContainingTypeView(store, symbolArg, snapshotId);
        else if (view.IsSurrounding)
            WriteSurroundingView(store, symbolArg, snapshotId, view.ContextLines);
        else
            WriteSourceView(store, symbolArg, snapshotId, view.ViewKind, viewArg, includeGenerated);
    }

    private static void WriteMetadataView(SqliteIndexStore store, string symbolArg, string snapshotId)
    {
        var info = store.GetSymbolInfo(symbolArg, snapshotId);
        if (info == null)
        {
            Console.Error.WriteLine($"ERROR: Symbol '{symbolArg}' not found in snapshot '{snapshotId}'.");
            Environment.Exit(1);
        }

        var json = JsonSerializer.Serialize(new
        {
            symbolId = info!.SymbolId.Value,
            docCommentId = info.SymbolId.DocCommentId,
            assemblyIdentity = info.SymbolId.AssemblyIdentity,
            kind = info.Kind.ToString(),
            fullyQualifiedName = info.FullyQualifiedName,
            metadataJson = info.MetadataJson,
            declarationCount = info.DeclarationCount,
            isPartial = info.IsPartial,
            snapshotId
        }, new JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine(json);
    }

    private static void WriteContainingTypeView(SqliteIndexStore store, string symbolArg, string snapshotId)
    {
        var source = store.GetContainingTypeSource(symbolArg, snapshotId);
        if (source == null)
        {
            Console.Error.WriteLine($"ERROR: Containing type source not found for symbol '{symbolArg}'.");
            Environment.Exit(1);
        }
        Console.Write(source);
    }

    private static void WriteSurroundingView(SqliteIndexStore store, string symbolArg, string snapshotId, int contextLines)
    {
        var source = store.GetSurroundingLines(symbolArg, snapshotId, contextLines);
        if (source == null)
        {
            Console.Error.WriteLine($"ERROR: Surrounding lines not found for symbol '{symbolArg}'.");
            Environment.Exit(1);
        }
        Console.Write(source);
    }

    private static void WriteSourceView(SqliteIndexStore store, string symbolArg, string snapshotId, ViewKind viewKind, string viewArg, bool includeGenerated)
    {
        var source = store.GetSymbolSource(symbolArg, snapshotId, viewKind, includeGenerated);
        if (source == null)
        {
            Console.Error.WriteLine($"ERROR: Source not found for symbol '{symbolArg}' with view '{viewArg}'.");
            Environment.Exit(1);
        }
        Console.Write(source);
    }

    private static string? RequireArg(string[] args, string prefix, params string[] errorLines)
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

    private static string? GetArgValue(string[] args, string prefix)
    {
        return args.FirstOrDefault(a => a.StartsWith(prefix))?.Split('=', 2)[1];
    }
}
