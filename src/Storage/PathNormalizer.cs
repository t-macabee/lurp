namespace Lurp.Shared;

/// <summary>
///     The single owner of the "document paths are stored git-root-relative and
///     forward-slashed" invariant. Every site that persists, compares, or resolves a
///     document path routes through here, so the transform cannot drift between the
///     persistence layer and the extraction-scope guards : a divergence there is the
///     bug class <c>TRUST_KERNEL.md</c> already recorded once (a scope guard that
///     forgot to forward-slash silently drops edges).
/// </summary>
public static class PathNormalizer
{
    /// <summary>
    ///     Path equality and hashing for this OS: case-insensitive on Windows, ordinal
    ///     elsewhere. Git and the file system are case-insensitive on Windows and
    ///     case-sensitive on Linux, so a path set that merges <c>Foo.cs</c> and
    ///     <c>foo.cs</c> there drops an invalidation and diverges from a full rebuild.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    ///     The <see cref="StringComparison" /> counterpart of <see cref="PathComparer" />,
    ///     for path checks written with <c>string.Equals</c>, <c>StartsWith</c> or
    ///     <c>EndsWith</c>.
    /// </summary>
    public static StringComparison PathComparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     Rewrite a path to the persisted separator (forward slash), leaving it
    ///     otherwise unchanged.
    /// </summary>
    public static string ToForwardSlash(string path)
    {
        return path.Replace('\\', '/');
    }

    /// <summary>
    ///     Convert an absolute path to git-root-relative, forward-slashed form. The
    ///     git root is fully resolved and its trailing separators trimmed before the
    ///     relative computation, so callers may pass an unnormalized root.
    /// </summary>
    public static string ToGitRelative(string absolutePath, string gitRoot)
    {
        return ToGitRelativeFromNormalizedRoot(absolutePath, NormalizeRoot(gitRoot));
    }

    /// <summary>
    ///     Normalise a path for storage/comparison: forward-slash form, and only
    ///     absolutized against the current directory when it is not already rooted on
    ///     this platform. A path that is already rooted -- including a foreign-platform
    ///     absolute path such as a Windows drive path read on Linux -- is left
    ///     untouched, so it is not misinterpreted as relative and re-prefixed with the
    ///     current working directory (the cross-platform double-prefix defect).
    /// </summary>
    public static string NormalizeForStorage(string path)
    {
        var resolved = IsRooted(path) ? path : Path.GetFullPath(path);
        return ToForwardSlash(resolved);
    }

    /// <summary>
    ///     Returns true when <paramref name="path" /> is rooted on the current OS
    ///     (<see cref="Path.IsPathRooted" />), or when it is a Windows drive path
    ///     (<c>X:/</c> or <c>X:\</c>, any letter) or a UNC path
    ///     (<c>\\server\share</c> or <c>//server/share</c>), so that manifests
    ///     written on Windows can be read on Linux without being re-prefixed with
    ///     the current working directory.
    /// </summary>
    private static bool IsRooted(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (Path.IsPathRooted(path))
        {
            return true;
        }

        // Windows drive path: "X:/" or "X:\"
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
            && (path[2] == '/' || path[2] == '\\'))
        {
            return true;
        }

        // UNC path: "\\server\share" or "//server/share"
        if (path.Length >= 2 && (path[0] == '\\' || path[0] == '/')
            && (path[1] == '\\' || path[1] == '/'))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Resolve and normalise a git-root directory: absolute path, trailing
    ///     separators stripped. Callers that need to normalise the root once and
    ///     reuse it across many documents pass the result to
    ///     <see cref="ToGitRelativeFromNormalizedRoot" />.
    /// </summary>
    public static string NormalizeRoot(string gitRoot)
    {
        return Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    ///     Convert an absolute path to git-root-relative, forward-slashed form,
    ///     using an already-normalised root from <see cref="NormalizeRoot" />.
    /// </summary>
    public static string ToGitRelativeFromNormalizedRoot(string absolutePath, string normalizedRoot)
    {
        var root = normalizedRoot + Path.DirectorySeparatorChar;
        return ToForwardSlash(Path.GetRelativePath(root, absolutePath));
    }
}