namespace Lurp.Shared;

/// <summary>
///     Identifies paths under <c>bin/</c> or <c>obj/</c> build output directories,
///     shared between the storage and workspace layers (D167.1).
/// </summary>
public static class BuildOutputPaths
{
    public static bool IsBuildOutputPath(string relPath)
    {
        var normalized = PathNormalizer.ToForwardSlash(relPath);

        if (normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
