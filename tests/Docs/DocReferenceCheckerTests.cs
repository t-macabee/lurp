using Lurp.Tests.Docs;

namespace Lurp.Tests;

/// <summary>
///     The checks of <see cref="DocReferenceChecker" /> on small hand-written inputs. Each expected
///     value is derived by hand from the GitHub Flavored Markdown anchor rule or from the input.
/// </summary>
public sealed class DocReferenceCheckerTests
{
    private static DocReferenceContext Context(
        string[] files,
        string[]? identifiers = null,
        string[]? lurpFlags = null,
        Dictionary<string, string>? externalNames = null,
        Dictionary<(string File, string Flag), string>? externalFlags = null) =>
        new(
            Root: Path.GetTempPath(),
            Files: files.ToHashSet(StringComparer.Ordinal),
            Directories: files.Select(path => Path.GetDirectoryName(path)!.Replace('\\', '/'))
                .Where(directory => directory.Length > 0).ToHashSet(StringComparer.Ordinal),
            FilesByName: files.ToLookup(path => Path.GetFileName(path), StringComparer.Ordinal),
            Identifiers: (identifiers ?? []).ToHashSet(StringComparer.Ordinal),
            LurpFlags: (lurpFlags ?? []).ToHashSet(StringComparer.Ordinal),
            ScriptFlags: new HashSet<string>(StringComparer.Ordinal),
            ExternalNames: externalNames ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ExternalFlags: externalFlags ?? new Dictionary<(string File, string Flag), string>());

    [Theory]
    [InlineData("MCP server (`--mode=serve`)", "mcp-server---modeserve")]
    [InlineData("Declared boundaries registry (capsule audit Task 7)", "declared-boundaries-registry-capsule-audit-task-7")]
    [InlineData("4.1 Framework adapters", "41-framework-adapters")]
    [InlineData("Documentation & license", "documentation--license")]
    [InlineData("`lurp_status` and `--mode=status`", "lurp_status-and---modestatus")]
    public void Slug_FollowsTheGitHubRule(string heading, string expected)
    {
        Assert.Equal(expected, DocReferenceChecker.Slug(heading));
    }

    [Fact]
    public void Anchors_NumberARepeatedHeading_AndSkipCodeFences()
    {
        const string markdown = "# Title\n## Title\n```\n# Not a heading\n```\n";

        var anchors = DocReferenceChecker.Anchors(markdown).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["title", "title-1"], anchors);
    }

    [Fact]
    public void AddCSharpNames_TakesIdentifiersAndStringWords_NotComments()
    {
        const string source =
            "// Stale_TestName\n" +
            "/// <summary>DocCommentName</summary>\n" +
            "class Real { string P = \"CustomProperty\"; void Works_AsDocumented() { } }\n";
        var names = new HashSet<string>(StringComparer.Ordinal);

        DocReferenceChecker.AddCSharpNames(source, names);

        Assert.Equal(["CustomProperty", "P", "Real", "Works_AsDocumented"], names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CheckLinks_ReportsAMissingFileAndAMissingAnchor_AndAcceptsTheRest()
    {
        var context = Context(["docs/a.md", "docs/b.md", "scripts/x/run.ps1"]);
        const string markdown =
            "[ok](b.md#target) [dir](../scripts/x/) [web](https://example.com/none)\n" +
            "[gone](c.md) [bad anchor](b.md#nothing) `[in code](none.md)`\n";

        var failures = DocReferenceChecker.CheckLinks("docs/a.md", markdown, context, _ => "## Target\n");

        Assert.Equal(
        [
            "docs/a.md:2: link target 'c.md' does not exist",
            "docs/a.md:2: anchor '#nothing' does not exist in docs/b.md",
        ], failures);
    }

    [Fact]
    public void CheckNames_ReportsAMissingPathAndAStaleTestName_AndAcceptsAListedName()
    {
        var context = Context(
            ["src/Real.cs", "tests/RealTests.cs"],
            identifiers: ["RealTests", "Works_AsDocumented"],
            externalNames: new Dictionary<string, string>(StringComparer.Ordinal) { ["CourseService.CreateAsync"] = "eNoteV2" });
        const string markdown =
            "`src/Real.cs` `RealTests.Works_AsDocumented` `CourseService.CreateAsync()`\n" +
            "`src/Gone.cs` `Old_TestName`\n" +
            "```\n`src/AlsoGone.cs`\n```\n";
        var used = new HashSet<string>(StringComparer.Ordinal);

        var failures = DocReferenceChecker.CheckNames("README.md", markdown, context, used);

        Assert.Equal(
        [
            "README.md:2: path 'src/Gone.cs' does not exist",
            "README.md:2: 'Old_TestName' names 'Old_TestName', which no source file declares or uses; if it names another repository or tool, list it in tests/Docs/external-names.txt",
        ], failures);
        Assert.Equal(["CourseService.CreateAsync"], used);
    }

    [Fact]
    public void CheckFlags_ReportsAnInventedFlag_AndAcceptsALurpFlagAndAListedFlag()
    {
        var context = Context(
            [],
            lurpFlags: ["--limit", "--mode"],
            externalFlags: new Dictionary<(string File, string Flag), string> { [("README.md", "--global")] = "dotnet tool install" });
        const string markdown = "`--limit=<n>` `lurp --mode=search --frobnicate=1` `--global`\n";
        var used = new HashSet<(string File, string Flag)>();

        var failures = DocReferenceChecker.CheckFlags("README.md", markdown, context, used);

        Assert.Equal(
            ["README.md:1: '--frobnicate' is not a flag of any mode; if it is a flag of another tool, list it in tests/Docs/external-flags.txt"],
            failures);
        Assert.Equal([("README.md", "--global")], used);
    }
}
