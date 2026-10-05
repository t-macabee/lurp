using Lurp.Parity.Shared;

namespace Lurp.Tests;

public sealed class TreeSnapshotTests
{
    [Fact]
    public void Diff_DetectsAddedFile()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            var before = TreeSnapshot.Record(dir);
            File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
            var after = TreeSnapshot.Record(dir);
            var diff = TreeSnapshot.Diff(before, after);
            Assert.Equal(["b.txt"], diff.Added);
            Assert.Empty(diff.Removed);
            Assert.Empty(diff.Changed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_DetectsRemovedFile()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "b");
            var before = TreeSnapshot.Record(dir);
            File.Delete(Path.Combine(dir, "b.txt"));
            var after = TreeSnapshot.Record(dir);
            var diff = TreeSnapshot.Diff(before, after);
            Assert.Empty(diff.Added);
            Assert.Equal(["b.txt"], diff.Removed);
            Assert.Empty(diff.Changed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_DetectsChangedFile_SameSizeDifferentContent()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
            var before = TreeSnapshot.Record(dir);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "world");
            var after = TreeSnapshot.Record(dir);
            var diff = TreeSnapshot.Diff(before, after);
            Assert.Empty(diff.Added);
            Assert.Empty(diff.Removed);
            var change = Assert.Single(diff.Changed);
            Assert.Equal("a.txt", change.Path);
            Assert.NotEqual(change.OldValue, change.NewValue);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_DetectsAddedEmptyDirectory()
    {
        var dir = NewTempDir();
        try
        {
            var before = TreeSnapshot.Record(dir);
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var after = TreeSnapshot.Record(dir);
            var diff = TreeSnapshot.Diff(before, after);
            Assert.Equal(["dir: sub"], diff.Added);
            Assert.Empty(diff.Removed);
            Assert.Empty(diff.Changed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_DetectsRemovedEmptyDirectory()
    {
        var dir = NewTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var before = TreeSnapshot.Record(dir);
            Directory.Delete(Path.Combine(dir, "sub"));
            var after = TreeSnapshot.Record(dir);
            var diff = TreeSnapshot.Diff(before, after);
            Assert.Empty(diff.Added);
            Assert.Equal(["dir: sub"], diff.Removed);
            Assert.Empty(diff.Changed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Diff_PassesWhenIdentical()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            var before = TreeSnapshot.Record(dir);
            var after = TreeSnapshot.Record(dir);
            var diff = TreeSnapshot.Diff(before, after);
            Assert.Empty(diff.Added);
            Assert.Empty(diff.Removed);
            Assert.Empty(diff.Changed);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lurp-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
