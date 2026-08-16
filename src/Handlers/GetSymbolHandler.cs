using Lurp.Workspace;
using System.Globalization;
using System.Text.Json;

namespace Lurp.Handlers;

internal static class GetSymbolHandler
{
    public static void Run(string[] args)
    {
        var symbolArg = HandlerBootstrap.RequireArg(args, "--symbol=", "ERROR: --symbol=<symbolId> is required for --mode=get-symbol.");
        var viewArg = HandlerBootstrap.RequireArg(args, "--view=", "ERROR: --view=<view-kind> is required for --mode=get-symbol.",
            "  Valid values: metadata, signature, body, declaration, containing-type, surrounding");

        var contextLinesArg = HandlerBootstrap.GetArgValue(args, "--context-lines=");
        var includeGenerated = args.Contains("--include-generated");

        HandlerBootstrap.WithStore(args, HandlerBootstrap.GetArgValue(args, "--snapshot="), (store, snapshotId) =>
        {
            var view = ResolveViewSelection(viewArg!, contextLinesArg);
            symbolArg = HandlerBootstrap.ResolveSymbolArg(store, symbolArg!, snapshotId);

            // Resolved once for every view: the raw-source views carry freshness on
            // stderr plus the exit code only, while the metadata view additionally
            // embeds the block in its JSON payload.
            var freshness = HandlerBootstrap.ResolveFreshness(args, store, snapshotId);
            WriteRequestedView(store, view, symbolArg!, snapshotId, viewArg!, includeGenerated, freshness);
        });
    }

    private static ViewSelection ResolveViewSelection(string viewArg, string? contextLinesArg)
    {
        switch (viewArg.ToLowerInvariant())
        {
            case "metadata":
                return new ViewSelection(ViewKind.Declaration, true, false, false, 3);
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
                HandlerBootstrap.Fail($"ERROR: Unknown view kind '{viewArg}'." + Environment.NewLine +
                                      "  Valid values: metadata, signature, body, declaration, containing-type, surrounding");
                return new ViewSelection(ViewKind.Declaration, false, false, false, 3);
        }
    }

    private static void WriteRequestedView(IDeclarationStore store, ViewSelection view, string symbolArg, string snapshotId, string viewArg, bool includeGenerated, FreshnessStamp freshness)
    {
        if (view.IsMetadataView)
            WriteMetadataView(store, symbolArg, snapshotId, freshness);
        else if (view.IsContainingType)
            WriteContainingTypeView(store, symbolArg, snapshotId);
        else if (view.IsSurrounding)
            WriteSurroundingView(store, symbolArg, snapshotId, view.ContextLines);
        else
            WriteSourceView(store, symbolArg, snapshotId, view.ViewKind, viewArg, includeGenerated);
    }

    private static void WriteMetadataView(IDeclarationStore store, string symbolArg, string snapshotId, FreshnessStamp freshness)
    {
        var info = store.GetSymbolInfo(symbolArg, snapshotId);
        if (info == null) HandlerBootstrap.Fail($"ERROR: Symbol '{symbolArg}' not found in snapshot '{snapshotId}'.");

        var locations = store.GetDeclarationLocations(symbolArg, snapshotId, true);

        var json = JsonSerializer.Serialize(new
        {
            symbol_id = info!.SymbolId.Value,
            doc_comment_id = info.SymbolId.DocCommentId,
            assembly_identity = info.SymbolId.AssemblyIdentity,
            kind = info.Kind.ToString(),
            fully_qualified_name = info.FullyQualifiedName,
            metadata_json = info.MetadataJson,
            declaration_count = info.DeclarationCount,
            is_partial = info.IsPartial,
            snapshot_id = snapshotId,
            freshness = HandlerBootstrap.FreshnessJson(freshness),
            locations
        }, HandlerBootstrap.IndentedJson);

        Console.WriteLine(json);
    }

    private static void WriteContainingTypeView(IDeclarationStore store, string symbolArg, string snapshotId)
    {
        var source = store.GetContainingTypeSource(symbolArg, snapshotId);
        if (source == null) HandlerBootstrap.Fail($"ERROR: Containing type source not found for symbol '{symbolArg}'.");
        Console.Write(source);
    }

    private static void WriteSurroundingView(IDeclarationStore store, string symbolArg, string snapshotId, int contextLines)
    {
        var source = store.GetSurroundingLines(symbolArg, snapshotId, contextLines);
        if (source == null) HandlerBootstrap.Fail($"ERROR: Surrounding lines not found for symbol '{symbolArg}'.");
        Console.Write(source);
    }

    private static void WriteSourceView(IDeclarationStore store, string symbolArg, string snapshotId, ViewKind viewKind, string viewArg, bool includeGenerated)
    {
        var source = store.GetSymbolSource(symbolArg, snapshotId, viewKind, includeGenerated);
        if (source == null) HandlerBootstrap.Fail($"ERROR: Source not found for symbol '{symbolArg}' with view '{viewArg}'.");
        Console.Write(source);
    }

    private sealed record ViewSelection(ViewKind ViewKind, bool IsMetadataView, bool IsContainingType, bool IsSurrounding, int ContextLines);
}