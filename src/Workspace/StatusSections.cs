namespace Lurp.Workspace;

/// <summary>
///     The set of sections a status request asks for. <see cref="Manifest" /> is true
///     whenever any part other than <c>freshness</c> is present; the other three mirror
///     the CLI <c>--detail=</c> flags and combine as a union (B51).
/// </summary>
public readonly record struct StatusSectionSet(bool Manifest, bool Documents, bool References, bool Completeness);

/// <summary>
///     The single parser for the <c>sections</c> (MCP <c>lurp_status</c>) and
///     <c>--detail=</c> (CLI <c>status</c>) vocabularies. Both surfaces accept the same
///     comma-separated, case-insensitive list; unknown values are errors (B51).
/// </summary>
internal static class StatusSections
{
    /// <summary>
    ///     Parses a comma-separated section list. <paramref name="allowSurfaceNames" /> is
    ///     true for MCP, which also accepts <c>freshness</c> and <c>manifest</c>. Returns
    ///     false with the first unknown part in <paramref name="badValue" />, or with the
    ///     whole list text when the list has no parts at all.
    /// </summary>
    public static bool TryParse(string list, bool allowSurfaceNames, out StatusSectionSet set, out string? badValue)
    {
        set = default;
        badValue = null;

        var parts = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            badValue = list;
            return false;
        }

        bool manifest = false, documents = false, references = false, completeness = false;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "freshness":
                    if (!allowSurfaceNames)
                    {
                        badValue = part;
                        return false;
                    }

                    break;
                case "manifest":
                    if (!allowSurfaceNames)
                    {
                        badValue = part;
                        return false;
                    }

                    manifest = true;
                    break;
                case "documents":
                    manifest = true;
                    documents = true;
                    break;
                case "references":
                    manifest = true;
                    references = true;
                    break;
                case "completeness":
                    manifest = true;
                    completeness = true;
                    break;
                case "all":
                    manifest = true;
                    documents = true;
                    references = true;
                    completeness = true;
                    break;
                default:
                    badValue = part;
                    return false;
            }
        }

        set = new StatusSectionSet(manifest, documents, references, completeness);
        return true;
    }

    /// <summary>The section names for an error message, written once for both surfaces.</summary>
    public static string Vocabulary(bool allowSurfaceNames) => allowSurfaceNames
        ? "freshness, manifest, documents, references, completeness or all"
        : "documents, references, completeness or all";
}
