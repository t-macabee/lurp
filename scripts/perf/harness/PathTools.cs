namespace Lurp.PerfHarness;

internal static class PathTools
{
    public static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Lurp.slnx")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    public static bool IsInside(string candidate, string root)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var rootNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        if (string.Equals(normalized, rootNormalized, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalized.StartsWith(rootNormalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
