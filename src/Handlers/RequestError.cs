using System.Globalization;

namespace Lurp.Handlers;

internal enum RequestErrorKind
{
    Missing,
    NotPositiveInteger,
    NotOneOf,
    EmptyItem,
    NoNames,
    UnknownName,
    BothAnchors,
    NoAnchor,
    HalfPair,
    InvalidCursor,
    NoSymbolAtLocation
}

/// <summary>
///     Structured request-validation failure shared by the CLI and MCP surfaces (D179).
///     The request parser returns a <see cref="RequestError" />; each surface formats it
///     once with its own parameter naming (<c>--flag-name</c> vs <c>property_name</c>).
/// </summary>
internal sealed record RequestError(
    RequestErrorKind Kind,
    FlagSpec Parameter,
    string? Value = null,
    IReadOnlyList<string>? Allowed = null,
    string? ItemLabel = null,
    string? Message = null,
    FlagSpec? SecondaryParameter = null,
    FlagSpec? TertiaryParameter = null,
    string? File = null,
    int? Line = null)
{
    internal static string FlagName(FlagSpec spec) => spec.Name.TrimEnd('=');

    internal static string PropertyName(FlagSpec spec) =>
        FlagName(spec).TrimStart('-').Replace('-', '_');

    internal string FormatCli() => "ERROR: " + FormatMessage(FlagName);

    internal string FormatMcp() => FormatMessage(PropertyName);

    private string FormatMessage(Func<FlagSpec, string> nameOf)
    {
        var name = nameOf(Parameter);
        return Kind switch
        {
            RequestErrorKind.Missing => $"{name} is required.",
            RequestErrorKind.NotPositiveInteger => $"{name} must be a positive integer.",
            RequestErrorKind.NotOneOf when Parameter == FlagCatalog.Direction =>
                $"Invalid direction '{Value}'. Use 'upstream' or 'downstream'.",
            RequestErrorKind.NotOneOf when Parameter == FlagCatalog.Tier =>
                $"unknown {name} '{Value}'. Valid tiers: {string.Join(", ", Allowed!)}.",
            RequestErrorKind.NotOneOf =>
                $"{name} must be one of: {string.Join(", ", Allowed!)}.",
            RequestErrorKind.EmptyItem =>
                $"{name} contains an empty item.",
            RequestErrorKind.NoNames =>
                $"{name} must name at least one {ItemLabel}. Use one of: {string.Join(", ", Allowed!)} (case-insensitive).",
            RequestErrorKind.UnknownName =>
                $"Unknown {ItemLabel} '{Value}' in {name}. Use one of: {string.Join(", ", Allowed!)} (case-insensitive).",
            RequestErrorKind.HalfPair =>
                $"{name} and {nameOf(SecondaryParameter!)} must be given together.",
            RequestErrorKind.NoAnchor =>
                $"Either {name} or {nameOf(SecondaryParameter!)} with {nameOf(TertiaryParameter!)} is required.",
            RequestErrorKind.BothAnchors =>
                $"Provide either {name} or {nameOf(SecondaryParameter!)} with {nameOf(TertiaryParameter!)}, not both.",
            RequestErrorKind.InvalidCursor =>
                Message ?? $"{name} is not a valid continuation token.",
            RequestErrorKind.NoSymbolAtLocation =>
                $"no symbol found at {File}:{Line}; {name} needs an anchor symbol.",
            _ => throw new InvalidOperationException($"Unhandled request error kind: {Kind}.")
        };
    }

    internal static bool TryParsePositiveInt(string? raw, int defaultValue, FlagSpec parameter, out int value, out RequestError? error)
    {
        if (string.IsNullOrEmpty(raw))
        {
            value = defaultValue;
            error = null;
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 1)
        {
            error = new RequestError(RequestErrorKind.NotPositiveInteger, parameter);
            return false;
        }

        error = null;
        return true;
    }

    internal static bool TryDecodeAndValidateCursor(
        string? rawCursor,
        string snapshotId,
        string fingerprint,
        string cursorKind,
        out int offset,
        out RequestError? error)
    {
        offset = 0;
        error = null;
        if (string.IsNullOrEmpty(rawCursor))
            return true;

        var cursor = SequenceCursor.TryDecode(rawCursor);
        if (cursor == null)
        {
            error = new RequestError(RequestErrorKind.InvalidCursor, FlagCatalog.Cursor);
            return false;
        }

        try
        {
            cursor.Validate(snapshotId, fingerprint, cursorKind);
        }
        catch (ArgumentException ex)
        {
            error = new RequestError(RequestErrorKind.InvalidCursor, FlagCatalog.Cursor, Message: ex.Message);
            return false;
        }

        offset = cursor.Offset;
        return true;
    }
}
