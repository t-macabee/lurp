using System.Globalization;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal sealed record ContextBinding(
    string? ResolvedSymbolId,
    string? Fingerprint,
    int Offset);

internal sealed record ContextRequest(
    string? Symbol,
    string? File,
    int? Line,
    ContextIntent Intent,
    int? ContentBudget,
    int MaxHops,
    string? Scope,
    IReadOnlyList<string> AffectedProjects,
    bool IncludeGenerated,
    bool CompletenessDetail,
    string? Tier,
    int TierLimit,
    string? Cursor)
{
    internal const string CursorKind = "capsule-tier";
    internal const int DefaultTierLimit = 25;
    internal const int DefaultMaxHops = 3;
    internal const int DefaultBudget = 8000;

    // A type anchor's callee/caller tiers scale with member fan-out, so a capsule
    // for a type routinely needs more room than a method anchor's. This default
    // only applies when the caller omits --content-budget=; an explicit budget is always
    // honored as-is.
    internal const int DefaultTypeAnchorBudget = 16000;

    internal static (ContextRequest? Request, RequestError? Error) Parse(
        string? symbol,
        string? file,
        string? line,
        string? intent,
        string? contentBudget,
        string? maxHops,
        string? scope,
        string[]? affectedProjects,
        bool includeGenerated,
        bool completenessDetail,
        string? tier,
        string? tierLimit,
        string? cursor)
    {
        var normalizedFile = HandlerBootstrap.NormalizeDocumentPath(file);
        var hasSymbol = !string.IsNullOrEmpty(symbol);
        var hasFileArg = !string.IsNullOrEmpty(normalizedFile);
        var hasLineArg = !string.IsNullOrEmpty(line);

        // D86: check half-pair first, then missing anchor, then both anchors.
        if (hasFileArg != hasLineArg)
            return (null, new RequestError(
                RequestErrorKind.HalfPair,
                FlagCatalog.File,
                SecondaryParameter: FlagCatalog.Line));

        if (!hasSymbol && !hasFileArg)
            return (null, new RequestError(
                RequestErrorKind.NoAnchor,
                FlagCatalog.Symbol,
                SecondaryParameter: FlagCatalog.File,
                TertiaryParameter: FlagCatalog.Line));

        if (hasSymbol && hasFileArg)
            return (null, new RequestError(
                RequestErrorKind.BothAnchors,
                FlagCatalog.Symbol,
                SecondaryParameter: FlagCatalog.File,
                TertiaryParameter: FlagCatalog.Line));

        var intentText = intent ?? "inspect";
        if (!ContextHandler.TryParseIntent(intentText, out var parsedIntent))
            return (null, new RequestError(
                RequestErrorKind.NotOneOf,
                FlagCatalog.Intent,
                Value: intentText,
                Allowed: FlagCatalog.Intent.AllowedValues));

        int? parsedBudget = null;
        if (!string.IsNullOrEmpty(contentBudget))
        {
            if (!RequestError.TryParsePositiveInt(contentBudget, DefaultBudget, FlagCatalog.ContentBudget, out var b, out var budgetError))
                return (null, budgetError);
            parsedBudget = b;
        }

        if (!RequestError.TryParsePositiveInt(maxHops, DefaultMaxHops, FlagCatalog.MaxHops, out var parsedMaxHops, out var hopsError))
            return (null, hopsError);

        int? parsedLine = null;
        if (hasFileArg)
        {
            if (!RequestError.TryParsePositiveInt(line, 1, FlagCatalog.Line, out var ln, out var lineError))
                return (null, lineError);
            parsedLine = ln;
        }

        if (!string.IsNullOrEmpty(tier) && Array.IndexOf(ContextAssembler.TierNames, tier) < 0)
            return (null, new RequestError(
                RequestErrorKind.NotOneOf,
                FlagCatalog.Tier,
                Value: tier,
                Allowed: ContextAssembler.TierNames));

        if (!RequestError.TryParsePositiveInt(tierLimit, DefaultTierLimit, FlagCatalog.TierLimit, out var parsedTierLimit, out var tierLimitError))
            return (null, tierLimitError);

        var projects = affectedProjects is { Length: > 0 }
            ? affectedProjects.Where(static v => !string.IsNullOrWhiteSpace(v)).ToArray()
            : [];

        return (new ContextRequest(
            symbol,
            normalizedFile,
            parsedLine,
            parsedIntent,
            parsedBudget,
            parsedMaxHops,
            scope,
            projects,
            includeGenerated,
            completenessDetail,
            string.IsNullOrEmpty(tier) ? null : tier,
            parsedTierLimit,
            cursor), null);
    }

    internal (ContextBinding? Binding, RequestError? Error) Bind(SqliteIndexStore store, string snapshotId)
    {
        string? resolvedSymbol = null;
        if (!string.IsNullOrEmpty(Symbol))
            resolvedSymbol = HandlerBootstrap.ResolveSymbolArg(store, Symbol, snapshotId, IncludeGenerated);

        if (Tier is null)
            return (new ContextBinding(resolvedSymbol, null, 0), null);

        if (resolvedSymbol is null)
        {
            resolvedSymbol = store.NavigateToLocation(File!, Line!.Value, snapshotId, IncludeGenerated)?.SymbolId;
            if (string.IsNullOrEmpty(resolvedSymbol))
                return (null, new RequestError(
                    RequestErrorKind.NoSymbolAtLocation,
                    FlagCatalog.Tier,
                    File: File,
                    Line: Line));
        }

        var fingerprint = SequenceCursor.ComputeFingerprint(
            resolvedSymbol,
            Tier,
            MaxHops.ToString(CultureInfo.InvariantCulture),
            IncludeGenerated.ToString());

        if (!RequestError.TryDecodeAndValidateCursor(Cursor, snapshotId, fingerprint, CursorKind, out var offset, out var cursorError))
            return (null, cursorError);

        return (new ContextBinding(resolvedSymbol, fingerprint, offset), null);
    }
}
