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

    public static string? CheckOutsideRepo(string flag, string path, bool isFile, params string?[] knownRoots)
    {
        var directory = isFile ? Path.GetDirectoryName(path) ?? path : path;
        var root = knownRoots.FirstOrDefault(root => root != null && IsInside(path, root))
            ?? FindRepoRoot(directory);
        return root == null ? null : $"{flag} '{path}' is inside the Lurp repo at '{root}'. Use scratch space outside the repo.";
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
