using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
/// Pins the canonical path identity the default cache directory is keyed by. A
/// change to the key moves every user's default index directory and leaves the
/// old cache behind, so these tests fail before that can ship silently.
/// </summary>
public sealed class LurpCacheTests
{
    private const string FixedSolutionPath = @"C:\lurp-pin\Fixed.sln";

    [SkippableFact]
    public void CanonicalKey_FixedPath_IsLowerCasedFullPath()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The fixed path is a Windows path.");

        Assert.Equal(@"c:\lurp-pin\fixed.sln", PathIdentity.CanonicalKey(FixedSolutionPath));
    }

    [SkippableFact]
    public void ResolveSolutionCacheDir_FixedPath_PinsDirectoryName()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The fixed path is a Windows path.");

        // A change here moves every user's default index directory; do not update
        // the literal to make a test pass.
        const string expectedHash = "106400a25b2c";

        var cacheDir = LurpCache.ResolveSolutionCacheDir(FixedSolutionPath);

        Assert.Equal(expectedHash, Path.GetFileName(cacheDir));
        Assert.Equal("lurp", Path.GetFileName(Path.GetDirectoryName(cacheDir)));
    }

    [SkippableFact]
    public void NormalizeKey_AndCanonicalKey_ReturnTheSameKey()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The fixed path is a Windows path.");

        var fromLock = IndexRunLock.NormalizeKey(FixedSolutionPath);
        var fromIdentity = PathIdentity.CanonicalKey(FixedSolutionPath);

        Assert.Equal(fromIdentity, fromLock);
        Assert.Equal(fromIdentity, IndexRunLock.NormalizeKey(@"c:\LURP-PIN\FIXED.SLN"));
        Assert.Equal(fromIdentity, PathIdentity.CanonicalKey(@"c:\LURP-PIN\FIXED.SLN"));
    }
}
