using Lurp.Storage;
using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class DeadCandidateLadderTests
{
    [Fact]
    public void CliReference_HasTheLadderAndResponseSections()
    {
        var path = Path.Combine(TestRepoRoot.Find(), "docs/CLI_REFERENCE.md");
        var text = File.ReadAllText(path).Replace("\r\n", "\n");

        Assert.Equal("\n" + DeadCandidateLadder.Ladder + "\n", DocSections.Extract(text, "dead-candidates:ladder"));
        Assert.Equal("\n" + DeadCandidateLadder.Response + "\n", DocSections.Extract(text, "dead-candidates:response"));
    }
}
