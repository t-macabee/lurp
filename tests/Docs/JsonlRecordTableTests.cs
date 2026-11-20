using System.Text.RegularExpressions;
using Lurp.Handlers;

namespace Lurp.Tests.Docs;

public sealed class JsonlRecordTableTests
{
    [Fact]
    public void JsonlTableInDocs_MatchesCliOutputWriterItemTypes()
    {
        var markdown = File.ReadAllText(Path.Combine(TestRepoRoot.Find(), "docs", "CLI_REFERENCE.md"));
        const string heading = "### JSONL record types";
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "The JSONL record-types table is missing.");
        var end = markdown.IndexOf("\n### ", start + heading.Length, StringComparison.Ordinal);
        var section = markdown[start..(end < 0 ? markdown.Length : end)];

        var documented = Regex.Matches(section, @"\| `([a-z-]+)` \| `meta` \| `([a-z]+)` \|")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        Assert.Equal(CliOutputWriter.JsonlItemTypes.OrderBy(pair => pair.Key), documented.OrderBy(pair => pair.Key));

        var registryModes = Program.ModeRegistry
            .Where(mode => mode.Specs.Any(flag => flag.Name == FlagCatalog.Output.Name && flag.AllowedValues?.Contains("jsonl") == true))
            .Select(mode => mode.Name);
        Assert.Equal(registryModes.Order(), documented.Keys.Order());
        Assert.DoesNotMatch("""\{"type":"(?:meta|symbol)"\s*(?:\}|,\s*[^"\s])""", markdown);
    }
}
