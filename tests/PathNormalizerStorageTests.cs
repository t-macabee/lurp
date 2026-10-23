using Lurp.Shared;
using Lurp.Workspace;
using Xunit;

namespace Lurp.Tests;

public sealed class PathNormalizerStorageTests
{
    [Fact]
    public void NormalizeForStorage_preserves_rooted_foreign_path_without_cwd_prefix()
    {
        // A Windows-style absolute path read on Linux is rooted (drive letter + colon)
        // and must be left untouched, never re-prefixed with the current directory.
        const string foreignRoot = "C:/Users/someone/Desktop/SomeProject/App";

        var result = PathNormalizer.NormalizeForStorage(foreignRoot);

        Assert.Equal(foreignRoot, result);
        Assert.DoesNotContain(Path.GetFullPath(Directory.GetCurrentDirectory()), result);
    }

    [Fact]
    public void NormalizeForStorage_resolves_relative_path_against_cwd()
    {
        var result = PathNormalizer.NormalizeForStorage("some/relative/path");

        Assert.Contains(PathNormalizer.NormalizeForStorage(Directory.GetCurrentDirectory()), result);
    }

    [Fact]
    public void WorkspaceId_Create_preserves_rooted_git_root_without_double_prefix()
    {
        const string gitRoot = "C:/Users/someone/Desktop/SomeProject/App";
        const string solutionPath = "C:/Users/someone/Desktop/SomeProject/App/App.sln";

        var id = WorkspaceId.Create(gitRoot, solutionPath);

        Assert.Equal(gitRoot, id.GitRoot);
        Assert.Equal("workspace://C:/Users/someone/Desktop/SomeProject/App/App.sln", id.Value);
        Assert.DoesNotContain(Path.GetFullPath(Directory.GetCurrentDirectory()), id.Value);
    }

    [Fact]
    public void NormalizeForStorage_preserves_linux_rooted_path_on_every_os()
    {
        const string linuxRoot = "/home/someone/Desktop/SomeProject/App";

        var result = PathNormalizer.NormalizeForStorage(linuxRoot);

        Assert.Equal(linuxRoot, result);
        Assert.DoesNotContain(Path.GetFullPath(Directory.GetCurrentDirectory()), result);
    }

    [Fact]
    public void NormalizeForStorage_preserves_unc_path_on_every_os()
    {
        const string uncRoot = "//server/share/SomeProject/App";

        var result = PathNormalizer.NormalizeForStorage(uncRoot);

        Assert.Equal(uncRoot, result);
    }

    [Fact]
    public void WorkspaceId_Create_preserves_linux_rooted_git_root_without_double_prefix()
    {
        const string gitRoot = "/home/someone/Desktop/SomeProject/App";
        const string solutionPath = "/home/someone/Desktop/SomeProject/App/App.sln";

        var id = WorkspaceId.Create(gitRoot, solutionPath);

        Assert.Equal(gitRoot, id.GitRoot);
        Assert.Equal("workspace:///home/someone/Desktop/SomeProject/App/App.sln", id.Value);
        Assert.DoesNotContain(Path.GetFullPath(Directory.GetCurrentDirectory()), id.Value);
    }

    [Fact]
    public void WorkspaceId_Create_preserves_unc_git_root_without_double_prefix()
    {
        const string gitRoot = "//server/share/SomeProject/App";
        const string solutionPath = "//server/share/SomeProject/App/App.sln";

        var id = WorkspaceId.Create(gitRoot, solutionPath);

        Assert.Equal(gitRoot, id.GitRoot);
        Assert.Equal("workspace:////server/share/SomeProject/App/App.sln", id.Value);
        Assert.DoesNotContain(Path.GetFullPath(Directory.GetCurrentDirectory()), id.Value);
    }

    [Fact]
    public void WorkspaceId_Create_root_relative_solution_preserves_value_uri_shape()
    {
        const string gitRoot = "C:/Users/someone/Desktop/SomeProject/App";
        const string relativeSln = "App.sln";

        var id = WorkspaceId.Create(gitRoot, relativeSln, solutionPathIsRootRelative: true);

        Assert.Equal("workspace://C:/Users/someone/Desktop/SomeProject/App/App.sln", id.Value);
        Assert.DoesNotContain(Path.GetFullPath(Directory.GetCurrentDirectory()), id.Value);
    }
}
