using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     D107 and D121: the doc tests read every doc from git, not from a hand list. The expected
///     files come from <c>git ls-files '*.md'</c> at 251355a.
/// </summary>
public sealed class DocFilesTests
{
    [Fact]
    public void Markdown_HoldsTheDocsThatTheHandListsLeftOut()
    {
        var docs = DocFiles.Markdown(TestRepoRoot.Find());

        Assert.Contains("scripts/README.md", docs);
        Assert.Contains("scripts/perf/README.md", docs);
        Assert.Contains("perf/baselines/README.md", docs);
        Assert.Contains("tests/Fixtures/GroundTruth/README.md", docs);
        Assert.Contains("docs/RELEASE_NOTES_3.6.1.md", docs);
    }

    [Fact]
    public void Markdown_LeavesOutTheAuditNotes()
    {
        var docs = DocFiles.Markdown(TestRepoRoot.Find());

        Assert.Contains("notes/TRUST_KERNEL.md", docs);
        Assert.DoesNotContain(docs, path => path.StartsWith("notes/audit/", StringComparison.Ordinal));
    }

    [Fact]
    public void Live_LeavesOutOnlyTheReleaseNotes()
    {
        var root = TestRepoRoot.Find();

        var removed = DocFiles.Markdown(root).Except(DocFiles.Live(root)).ToList();

        Assert.Contains("docs/RELEASE_NOTES_2.0.0.md", removed);
        Assert.All(removed, path => Assert.Matches(@"^docs/RELEASE_NOTES_\d+\.\d+\.\d+\.md$", path));
    }
}
