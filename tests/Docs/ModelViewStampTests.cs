using System.Text.RegularExpressions;

namespace Lurp.Tests;

public sealed class ModelViewStampTests
{
    [Fact]
    public void ReadmeSentence_MatchesThePageStamps()
    {
        var root = TestRepoRoot.Find();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var page = File.ReadAllText(Path.Combine(root, "docs/MODEL_VIEW.html"));
        var paragraph = Regex.Match(readme, @"(?m)^## \[What the model sees\]\([^\r\n]+\)\r?\n\r?\n(?<value>[^\r\n]+(?:\r?\n[^\r\n]+)*)").Groups["value"].Value;
        var footer = Regex.Match(page, """(?s)<footer class="chart-footer">(?<value>.*?)</footer>""").Groups["value"].Value;

        AssertStamp("tool version", paragraph, @"A point-in-time map of one Lurp (?<value>\d+\.\d+\.\d+) index",
            page, @"<b>tool</b> (?<value>\d+\.\d+\.\d+)</span>");
        AssertStamp("source commit", paragraph, @" at `(?<value>[0-9a-f]+)`", footer, @"at commit <code>(?<value>[0-9a-f]+)</code>");
        AssertStamp("project count", paragraph, @"\b(?<value>\d+) projects\)", page, @"<b>projects</b> (?<value>\d+)</span>");
        Assert.DoesNotContain("with uncommitted source changes", footer, StringComparison.Ordinal);
    }

    private static void AssertStamp(string name, string readme, string readmePattern, string page, string pagePattern)
    {
        var expected = Regex.Match(readme, readmePattern).Groups["value"].Value;
        var actual = Regex.Match(page, pagePattern).Groups["value"].Value;

        Assert.True(expected.Length > 0 && expected == actual,
            $"MODEL_VIEW {name}. README: '{expected}'. Page: '{actual}'.");
    }
}
