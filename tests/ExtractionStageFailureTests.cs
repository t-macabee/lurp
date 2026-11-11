using Lurp.Adapters;
using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     D34: only the declaration stage is required. A failing stage keeps the facts of the
///     other stages and records one <c>extractor_failure</c> row on each document.
/// </summary>
public sealed class ExtractionStageFailureTests : InMemoryTestBase
{
    [Fact]
    public async Task AdapterStageThrows_KeepsTheOtherFacts_AndRecordsTheFailureOnEachDocument()
    {
        var extraction = await ExtractAsync(
            new Dictionary<string, string>
            {
                ["Caller.cs"] = """
                                namespace N;
                                public class Caller
                                {
                                    public void Run(Callee c) { c.Work(); }
                                }
                                """,
                ["Callee.cs"] = """
                                namespace N;
                                public class Callee
                                {
                                    public void Work() { }
                                }
                                """
            },
            adapterProvider: _ => [new ThrowingAdapter()]);

        var failure = Assert.Single(extraction.Result.StageFailures);
        Assert.Equal("Adapter", failure.Stage);
        Assert.Equal("Throwing", failure.AdapterName);
        Assert.Equal("adapter boom", failure.Message);

        // The member-edge stage ran before the adapter and its fact is kept.
        extraction.AssertEdgeSet("Calls", ("global::N.Caller.Run", "global::N.Callee.Work"));

        var rows = extraction.Result.BindingIncompleteness
            .Where(static r => r.Reason == BindingIncompletenessReason.ExtractorFailure)
            .OrderBy(static r => r.DocumentPath, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["Callee.cs", "Caller.cs"], rows.Select(static r => Path.GetFileName(r.DocumentPath!)));
        Assert.All(rows, static r => Assert.Equal(1, r.Count));
    }

    private sealed class ThrowingAdapter : IFrameworkAdapter
    {
        public string Name => "Throwing";
        public string Version => "throwing-v1";
        public string Description => "Throws for every project.";

        public AdapterExtractionResult Extract(AdapterExtractionContext context) =>
            throw new InvalidOperationException("adapter boom");
    }
}
