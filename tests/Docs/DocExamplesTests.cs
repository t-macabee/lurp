using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class DocExamplesTests
{
    // --- extraction ---

    [Fact]
    public void Extract_TakesOnlyLurpLinesFromACommandFence()
    {
        const string markdown =
            "```bash\n" +
            "lurp --mode=grep --query=a --output-dir=./out\n" +
            "dotnet build\n" +
            "lurp --mode=search --query=b --output-dir=./out\n" +
            "```\n";

        var examples = DocExamples.Extract(markdown, "test.md");

        Assert.Equal(2, examples.Count);
        Assert.All(examples, e => Assert.Equal(DocExampleKind.Command, e.Kind));
        Assert.Collection(examples,
            first => Assert.Equal(2, first.Line),
            second => Assert.Equal(4, second.Line));
    }

    [Fact]
    public void Extract_IgnoresANonCommandFence()
    {
        const string markdown = "```json\n{\"command\": \"lurp --mode=grep --query=x\"}\n```\n";

        Assert.Empty(DocExamples.Extract(markdown, "test.md"));
    }

    [Fact]
    public void Extract_TakesAnInlineSpanThatStartsWithLurpMode()
    {
        const string markdown = "| Task | `lurp --mode=grep --query=x --output-dir=./out` |\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Equal(DocExampleKind.Command, example.Kind);
        Assert.Equal("lurp --mode=grep --query=x --output-dir=./out", example.Text);
    }

    [Fact]
    public void Extract_MarksAModeUsageLineAsUsage()
    {
        const string markdown = "```\n--mode=grep --query=x --output-dir=./out\n```\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Equal(DocExampleKind.Usage, example.Kind);
        Assert.Equal(2, example.Line);
    }

    [Fact]
    public void Extract_DropsATrailingComment()
    {
        const string markdown =
            "```bash\n" +
            "lurp --mode=pin-snapshot --snapshot=latest --output-dir=./out  # alias for --clear\n" +
            "```\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Equal("lurp --mode=pin-snapshot --snapshot=latest --output-dir=./out", example.Text);
    }

    [Fact]
    public void Extract_TakesAMarkerOnThePrecedingLine()
    {
        const string markdown =
            "<!-- doc-test: skip, needs a live server -->\n" +
            "```bash\n" +
            "lurp --mode=grep --query=x --output-dir=./out\n" +
            "```\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Equal("needs a live server", example.SkipReason);
        Assert.Null(example.MarkerProblem);
    }

    [Fact]
    public void Extract_TakesAMarkerOnTheSameLine()
    {
        const string markdown =
            "| Task | `lurp --mode=grep --query=x --output-dir=./out` <!-- doc-test: skip, inline reason --> |\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Equal("inline reason", example.SkipReason);
        Assert.Null(example.MarkerProblem);
    }

    [Fact]
    public void Extract_ReportsAMarkerWithABlankReason()
    {
        const string markdown =
            "<!-- doc-test: skip,   -->\n" +
            "```bash\n" +
            "lurp --mode=grep --query=x --output-dir=./out\n" +
            "```\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Null(example.SkipReason);
        Assert.NotNull(example.MarkerProblem);
        Assert.Contains("test.md:1", example.MarkerProblem!, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_UsesOneBasedLineNumbers()
    {
        const string markdown = "# title\n\n```bash\nlurp --mode=grep --query=x --output-dir=./out\n```\n";

        var example = Assert.Single(DocExamples.Extract(markdown, "test.md"));

        Assert.Equal(4, example.Line);
    }

    // --- Tier 1 ---

    [Fact]
    public void Tier1_AcceptsAValidCommand()
    {
        Assert.Empty(Validate("lurp --mode=grep --query=x --output-dir=./out"));
    }

    [Fact]
    public void Tier1_RejectsAnUnknownMode()
    {
        Assert.NotEmpty(Validate("lurp --mode=nope --output-dir=./out"));
    }

    [Fact]
    public void Tier1_RejectsAnUnknownFlagByName()
    {
        var failures = Validate("lurp --mode=grep --bogus=1 --query=x --output-dir=./out");

        Assert.Contains(failures, failure => failure.Contains("--bogus=", StringComparison.Ordinal));
    }

    [Fact]
    public void Tier1_RejectsADisallowedValue()
    {
        Assert.NotEmpty(Validate("lurp --mode=search --query=x --type=banana --output-dir=./out"));
    }

    [Fact]
    public void Tier1_AcceptsAPlaceholderValue()
    {
        Assert.Empty(Validate("lurp --mode=search --query=x --type=<all|source> --output-dir=./out"));
    }

    [Fact]
    public void Tier1_RejectsAMissingRequiredFlag()
    {
        Assert.NotEmpty(Validate("lurp --mode=grep --output-dir=./out"));
    }

    [Fact]
    public void Tier1_AcceptsVersion()
    {
        Assert.Empty(Validate("lurp --version"));
    }

    private static IReadOnlyList<string> Validate(string text, DocExampleKind kind = DocExampleKind.Command) =>
        DocExamples.Validate(new DocExample("test.md", 7, text, kind, null, null));
}
