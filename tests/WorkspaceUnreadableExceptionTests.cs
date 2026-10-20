using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class WorkspaceUnreadableExceptionTests
{
    [Fact]
    public void Constructor_KeepsInnerException()
    {
        var inner = new InvalidOperationException("SDK discovery failed");

        var ex = new WorkspaceUnreadableException("workspace unreadable", inner);

        Assert.Same(inner, ex.InnerException);
        Assert.Equal("workspace unreadable", ex.Message);
    }
}
