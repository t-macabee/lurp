namespace Lurp.Shared;

/// <summary>
///     The single owner of the "is this document generated?" path rule. It lives next to
///     <see cref="PathNormalizer" /> so the persistence layer and the extraction-scope
///     guards share one definition instead of two hand-synced copies.
/// </summary>
public static class GeneratedFilePaths
{
    /// <summary>
    ///     <c>ModelSnapshot.cs</c> (e.g. <c>AppDbContextModelSnapshot.cs</c>) is scaffolded by
    ///     <c>dotnet ef migrations add</c> alongside each migration's own <c>.Designer.cs</c>,
    ///     and hand-editing it is discouraged the same way hand-editing a designer file is —
    ///     so it counts as generated here even though, unlike <c>obj/**</c>, it is normally
    ///     committed to source control rather than gitignored.
    /// </summary>
    public static bool IsGenerated(string normalizedPath)
    {
        if (normalizedPath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        if (normalizedPath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/generated/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
