using Lurp.Storage;
using Lurp.Workspace;
using System.Globalization;

namespace Lurp.Tests;

/// <summary>
///     F10: the full-index summary prints the two counters with their current
///     names, and <c>symbols_in_snapshot</c> is the distinct-symbol count even
///     when a partial type is declared in more than one document.
/// </summary>
public sealed class IndexSummaryCounterTests : IntegrationTestBase
{
    private const string Project = "Core";

    private const string PartialPartOneSource = """
                                               namespace Core;

                                               public partial class PartialShape
                                               {
                                                   public int FromPartOne() => 1;
                                               }
                                               """;

    private const string PartialPartTwoSource = """
                                               namespace Core;

                                               public partial class PartialShape
                                               {
                                                   public int FromPartTwo() => 2;
                                               }
                                               """;

    private const string WidgetSource = """
                                        namespace Core;

                                        public class Widget
                                        {
                                            public int Value { get; set; }
                                        }
                                        """;

    [Fact]
    public async Task FullIndex_SummaryCountsExtractedRecordsAndDistinctSymbols()
    {
        CreateProject(Project, new Dictionary<string, string>
        {
            ["PartialShape.Part1.cs"] = PartialPartOneSource,
            ["PartialShape.Part2.cs"] = PartialPartTwoSource,
            ["Widget.cs"] = WidgetSource
        });

        using var store = OpenStore(DbPath);
        var sink = new CapturingOutputSink();
        await IndexRunner.RunAsync(store, SolutionPath, [], null, "full", false, sink, false, false, CancellationToken.None);
        var output = sink.Output.ToString();

        var summary = Assert.Single(output.Split('\n'), line => line.Contains("declarations_extracted_this_run:", StringComparison.Ordinal));
        Assert.Contains("symbols_in_snapshot:", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("declarations_in_snapshot", summary, StringComparison.Ordinal);

        var extracted = ParseCounter(summary, "declarations_extracted_this_run:");
        var snapshotSymbols = ParseCounter(summary, "symbols_in_snapshot:");
        Assert.True(extracted > snapshotSymbols,
            $"a partial type declared twice must add more records than symbols: {extracted} records, {snapshotSymbols} symbols");

        var snapshotId = store.LoadLatestSnapshot()!.SnapshotId;
        Assert.Equal(snapshotSymbols, store.CountSymbolsInSnapshot(snapshotId));

        // Positive control: PartialShape is one symbol with two declaration records.
        var partialIds = store.GetSymbolIdsInSnapshot(snapshotId)
            .Where(id => id.StartsWith("T:Core.PartialShape|", StringComparison.Ordinal))
            .ToList();
        var partialId = Assert.Single(partialIds);
        Assert.Equal(2, store.GetSymbolInfo(partialId, snapshotId)!.DeclarationCount);
    }

    private static int ParseCounter(string line, string name)
    {
        var rest = line[(line.IndexOf(name, StringComparison.Ordinal) + name.Length)..];
        var start = 0;
        while (start < rest.Length && char.IsWhiteSpace(rest[start]))
            start++;
        var end = start;
        while (end < rest.Length && char.IsAsciiDigit(rest[end]))
            end++;
        return int.Parse(rest[start..end], CultureInfo.InvariantCulture);
    }
}
