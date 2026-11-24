using System.Globalization;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Handlers;

internal sealed record ImpactBinding(
    string ResolvedSymbolId,
    string Fingerprint,
    int Offset);

internal sealed record ImpactRequest(
    string Symbol,
    ImpactDirection Direction,
    int MaxDepth,
    int Limit,
    IReadOnlyList<string>? CanonicalKinds,
    IReadOnlyList<string>? CanonicalProvenance,
    IReadOnlySet<string> AllowedKinds,
    HashSet<string>? AllowedProvenance,
    string? Cursor)
{
    internal const string CursorKind = "impact";
    internal const int DefaultLimit = 50;
    internal const int DefaultMaxDepth = 3;
    internal static readonly IReadOnlyList<string> EdgeKindNames = Enum.GetNames<EdgeKind>();

    internal static (ImpactRequest? Request, RequestError? Error) Parse(
        string? symbol,
        string? direction,
        string[]? kinds,
        string[]? provenance,
        string? maxDepth,
        string? limit,
        string? cursor)
    {
        if (string.IsNullOrEmpty(symbol))
            return (null, new RequestError(RequestErrorKind.Missing, FlagCatalog.Symbol));

        var directionText = direction ?? "downstream";
        if (!ImpactHandler.TryParseDirection(directionText, out var parsedDirection))
            return (null, new RequestError(
                RequestErrorKind.NotOneOf,
                FlagCatalog.Direction,
                Value: directionText,
                Allowed: FlagCatalog.Direction.AllowedValues));

        if (!RequestError.TryParsePositiveInt(maxDepth, DefaultMaxDepth, FlagCatalog.MaxDepth, out var parsedMaxDepth, out var depthError))
            return (null, depthError);

        if (!RequestError.TryParsePositiveInt(limit, DefaultLimit, FlagCatalog.Limit, out var parsedLimit, out var limitError))
            return (null, limitError);

        IReadOnlyList<string>? canonicalKinds = null;
        if (kinds is not null)
        {
            var outcome = ImpactHandler.TryParseNameFilter(kinds, EdgeKindNames, out canonicalKinds, out var unknownKind);
            if (outcome == ImpactHandler.NameFilterParseOutcome.EmptyItem)
                return (null, new RequestError(RequestErrorKind.EmptyItem, FlagCatalog.Kinds));
            if (outcome == ImpactHandler.NameFilterParseOutcome.UnknownName)
                return (null, new RequestError(
                    RequestErrorKind.UnknownName,
                    FlagCatalog.Kinds,
                    Value: unknownKind,
                    Allowed: EdgeKindNames,
                    ItemLabel: "edge kind"));
            if (canonicalKinds is null)
                return (null, new RequestError(
                    RequestErrorKind.NoNames,
                    FlagCatalog.Kinds,
                    Allowed: EdgeKindNames,
                    ItemLabel: "edge kind"));
        }

        IReadOnlyList<string>? canonicalProvenance = null;
        if (provenance is not null)
        {
            var outcome = ImpactHandler.TryParseNameFilter(provenance, Provenance.All, out canonicalProvenance, out var unknownProvenance);
            if (outcome == ImpactHandler.NameFilterParseOutcome.EmptyItem)
                return (null, new RequestError(RequestErrorKind.EmptyItem, FlagCatalog.Provenance));
            if (outcome == ImpactHandler.NameFilterParseOutcome.UnknownName)
                return (null, new RequestError(
                    RequestErrorKind.UnknownName,
                    FlagCatalog.Provenance,
                    Value: unknownProvenance,
                    Allowed: Provenance.All,
                    ItemLabel: "provenance"));
            if (canonicalProvenance is null)
                return (null, new RequestError(
                    RequestErrorKind.NoNames,
                    FlagCatalog.Provenance,
                    Allowed: Provenance.All,
                    ItemLabel: "provenance"));
        }

        IReadOnlySet<string> allowedKinds = canonicalKinds is not null
            ? new HashSet<string>(canonicalKinds, StringComparer.Ordinal)
            : ImpactReachability.DefaultTraversalKinds;
        HashSet<string>? allowedProvenance = canonicalProvenance is null
            ? null
            : new HashSet<string>(canonicalProvenance, StringComparer.Ordinal);

        return (new ImpactRequest(
            symbol,
            parsedDirection,
            parsedMaxDepth,
            parsedLimit,
            canonicalKinds,
            canonicalProvenance,
            allowedKinds,
            allowedProvenance,
            cursor), null);
    }

    internal (ImpactBinding? Binding, RequestError? Error) Bind(SqliteIndexStore store, string snapshotId)
    {
        var resolvedSymbolId = HandlerBootstrap.ResolveSymbolArg(store, Symbol, snapshotId, includeGenerated: true);

        var fingerprint = SequenceCursor.ComputeFingerprint(
            resolvedSymbolId,
            Direction.ToString(),
            MaxDepth.ToString(CultureInfo.InvariantCulture),
            CanonicalKinds is null ? null : string.Join(",", CanonicalKinds),
            CanonicalProvenance is null ? null : string.Join(",", CanonicalProvenance),
            "symbols");

        if (!RequestError.TryDecodeAndValidateCursor(Cursor, snapshotId, fingerprint, CursorKind, out var offset, out var cursorError))
            return (null, cursorError);

        return (new ImpactBinding(resolvedSymbolId, fingerprint, offset), null);
    }
}
