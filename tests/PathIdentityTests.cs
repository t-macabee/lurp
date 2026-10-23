using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class PathIdentityTests
{
    [SkippableFact]
    public void SymlinkedDirectory_SameCanonicalKey()
    {
        Skip.If(OperatingSystem.IsWindows(), "Linux only (symlinks).");

        var root = Path.Combine(Path.GetTempPath(), "lurp-pi-" + Guid.NewGuid());
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Equal(PathIdentity.CanonicalKey(target), PathIdentity.CanonicalKey(link));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(target); } catch { }
            try { Directory.Delete(root); } catch { }
        }
    }

    [SkippableFact]
    public void SymlinkInMiddleSegment_Resolves()
    {
        Skip.If(OperatingSystem.IsWindows(), "Linux only (symlinks).");

        var root = Path.Combine(Path.GetTempPath(), "lurp-pi-" + Guid.NewGuid());
        var target = Path.Combine(root, "target");
        var mid = Path.Combine(root, "mid");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(mid, target);
        try
        {
            var viaLink = Path.Combine(mid, "sub", "file.cs");
            var viaTarget = Path.Combine(target, "sub", "file.cs");
            Assert.Equal(PathIdentity.CanonicalKey(viaTarget), PathIdentity.CanonicalKey(viaLink));
        }
        finally
        {
            try { Directory.Delete(mid); } catch { }
            try { Directory.Delete(target); } catch { }
            try { Directory.Delete(root); } catch { }
        }
    }

    [SkippableFact]
    public void NonexistentTail_ResolvesExistingAncestor()
    {
        Skip.If(OperatingSystem.IsWindows(), "Linux only (symlinks).");

        var root = Path.Combine(Path.GetTempPath(), "lurp-pi-" + Guid.NewGuid());
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            var viaLink = Path.Combine(link, "noexist", "file.cs");
            var viaTarget = Path.Combine(target, "noexist", "file.cs");
            Assert.Equal(PathIdentity.CanonicalKey(viaTarget), PathIdentity.CanonicalKey(viaLink));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(target); } catch { }
            try { Directory.Delete(root); } catch { }
        }
    }

    [SkippableFact]
    public void IndexRunLock_OnePathForBothSpellings()
    {
        Skip.If(OperatingSystem.IsWindows(), "Linux only (symlinks).");

        var root = Path.Combine(Path.GetTempPath(), "lurp-pi-" + Guid.NewGuid());
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);
        try
        {
            var fromLink = IndexRunLock.NormalizeKey(link);
            var fromTarget = IndexRunLock.NormalizeKey(target);
            Assert.Equal(fromTarget, fromLink);
            Assert.Equal(IndexRunLock.GetLockFilePath(fromTarget), IndexRunLock.GetLockFilePath(fromLink));
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(target); } catch { }
            try { Directory.Delete(root); } catch { }
        }
    }
}
