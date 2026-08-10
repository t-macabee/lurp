namespace Lurp.Shared;

/// <summary>
/// The single owner of the "document paths are stored git-root-relative and
/// forward-slashed" invariant. Every site that persists, compares, or resolves a
/// document path routes through here, so the transform cannot drift between the
/// persistence layer and the extraction-scope guards : a divergence there is the
/// bug class <c>TRUST_KERNEL.md</c> already recorded once (a scope guard that
/// forgot to forward-slash silently drops edges).
/// </summary>
public static class PathNormalizer
{
    /// <summary>
    /// Rewrite a path to the persisted separator (forward slash), leaving it
    /// otherwise unchanged.
    /// </summary>
    public static string ToForwardSlash(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Convert an absolute path to git-root-relative, forward-slashed form. The
    /// git root is fully resolved and its trailing separators trimmed before the
    /// relative computation, so callers may pass an unnormalized root.
    /// </summary>
    public static string ToGitRelative(string absolutePath, string gitRoot)
    {
        var normalizedRoot = Path.GetFullPath(gitRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = normalizedRoot + Path.DirectorySeparatorChar;
        return ToForwardSlash(Path.GetRelativePath(root, absolutePath));
    }
}
