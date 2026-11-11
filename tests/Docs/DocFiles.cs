using System.Diagnostics;

namespace Lurp.Tests.Docs;

/// <summary>
///     The documentation files that the doc tests read. The list comes from git, so a new doc is
///     read without a hand edit (D107). A doc is a <c>*.md</c> file outside <c>notes/audit/</c>;
///     under <c>tests/Fixtures/</c> only a <c>README.md</c> is a doc (D121).
/// </summary>
internal static class DocFiles
{
    private const string ReleaseNotesPrefix = "docs/RELEASE_NOTES_";

    /// <summary>
    ///     Every file that git tracks or would track (not ignored), relative to
    ///     <paramref name="root" />, with <c>/</c> separators, in ordinal order. A deleted file that
    ///     is still in the git index is left out.
    /// </summary>
    internal static IReadOnlyList<string> Repository(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start git. The doc tests need git on PATH.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git ls-files failed with exit code {process.ExitCode} in '{root}': {error.Result.Trim()}");

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Every doc of the repository (see the class summary).</summary>
    internal static IReadOnlyList<string> Markdown(string root) =>
        Repository(root).Where(IsDoc).ToList();

    /// <summary>The docs that state the current behavior: every doc except the release notes (D109).</summary>
    internal static IReadOnlyList<string> Live(string root) =>
        Markdown(root).Where(path => !IsReleaseNotes(path)).ToList();

    internal static bool IsReleaseNotes(string path) =>
        path.StartsWith(ReleaseNotesPrefix, StringComparison.Ordinal)
        && path.EndsWith(".md", StringComparison.Ordinal)
        && !path[ReleaseNotesPrefix.Length..].Contains('/');

    private static bool IsDoc(string path) =>
        path.EndsWith(".md", StringComparison.Ordinal)
        && !path.StartsWith("notes/audit/", StringComparison.Ordinal)
        && (!path.StartsWith("tests/Fixtures/", StringComparison.Ordinal)
            || Path.GetFileName(path) == "README.md");
}
