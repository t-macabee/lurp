using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
///     T6: <see cref="GitIgnoreMatcher" /> compares patterns with
///     <see cref="PathNormalizer.PathComparison" />, so a match is case-sensitive where the
///     file system is (Linux). Git's <c>core.ignorecase</c> defaults to true on Windows and
///     false elsewhere, so matching "Build/" against "build/" there would ignore a document
///     the working tree keeps.
/// </summary>
public sealed class GitIgnoreMatcherTests : IDisposable
{
    private readonly string _root;

    public GitIgnoreMatcherTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lurp-gitignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        TestDatabase.DeleteDirectory(_root);
        GC.SuppressFinalize(this);
    }

    [SkippableFact]
    public void DirectoryPattern_MatchesOnlyTheExactCase_OnLinux()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows is case-insensitive; git's core.ignorecase defaults to true there.");

        var matcher = Load("Build/\n");

        Assert.True(matcher.IsIgnored("Build/Generated.cs"));
        Assert.False(matcher.IsIgnored("build/Generated.cs"));
    }

    [SkippableFact]
    public void FilePattern_MatchesOnlyTheExactCase_OnLinux()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows is case-insensitive; git's core.ignorecase defaults to true there.");

        var matcher = Load("Legacy.cs\n");

        Assert.True(matcher.IsIgnored("src/Legacy.cs"));
        Assert.False(matcher.IsIgnored("src/legacy.cs"));
    }

    [SkippableFact]
    public void DoubleStarPattern_MatchesOnlyTheExactCase_OnLinux()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows is case-insensitive; git's core.ignorecase defaults to true there.");

        var matcher = Load("Src/**/Gen/**/Model.cs\n");

        Assert.True(matcher.IsIgnored("Src/a/Gen/b/Model.cs"));
        Assert.False(matcher.IsIgnored("src/a/Gen/b/Model.cs"));
        Assert.False(matcher.IsIgnored("Src/a/gen/b/Model.cs"));
        Assert.False(matcher.IsIgnored("Src/a/Gen/b/model.cs"));
    }

    [SkippableFact]
    public void WildcardPattern_MatchesOnlyTheExactCase_OnLinux()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows is case-insensitive; git's core.ignorecase defaults to true there.");

        var matcher = Load("*.Designer.cs\n");

        Assert.True(matcher.IsIgnored("src/Form.Designer.cs"));
        Assert.False(matcher.IsIgnored("src/Form.designer.cs"));
    }

    private GitIgnoreMatcher Load(string content)
    {
        File.WriteAllText(Path.Combine(_root, ".gitignore"), content);
        return GitIgnoreMatcher.Load(_root);
    }
}
