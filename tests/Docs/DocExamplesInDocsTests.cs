using Lurp.Tests.Docs;

namespace Lurp.Tests;

public sealed class DocExamplesInDocsTests
{
    [Fact]
    public void EveryExampleInTheDocs_ParsesAgainstTheRegistry()
    {
        var root = DocCatalog.RepoRoot();
        var examples = DocExamples.ExtractAll(root);

        Assert.True(examples.Count >= 10,
            $"Expected at least 10 lurp examples across the docs, found {examples.Count}.");

        var failures = new List<string>();
        foreach (var example in examples)
        {
            if (example.MarkerProblem is not null)
                failures.Add(example.MarkerProblem);
            failures.AddRange(DocExamples.Validate(example));
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
