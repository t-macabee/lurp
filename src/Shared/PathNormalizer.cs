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
        var resolved = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        return ToForwardSlash(resolved);
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