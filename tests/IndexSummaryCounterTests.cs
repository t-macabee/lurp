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
    [Fact]
    public async Task FullIndex_SummaryCountsExtractedRecordsAndDistinctSymbols()
    {
        CreateProject(PartialShapeFixture.ProjectName, PartialShapeFixture.Sources);

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

    [Fact]
    public async Task GitIgnoredCompileItem_IsIndexed_AndExcludedObjCompileItem_IsCountedAndBucketed()
    {
        File.WriteAllText(Path.Combine(TestDir, ".gitignore"), "Secret.cs\n");

        CreateProject("Lib", new Dictionary<string, string>
        {
            ["Secret.cs"] = """
                            namespace Lib;

                            public static class Secret
                            {
                                public static int Value() => 7;
                            }
                            """
        });
        CreateProject("App", new Dictionary<string, string>
        {
            ["Caller.cs"] = """
                            namespace App;

                            public static class Caller
                            {
                                public static int Run() => Lib.Secret.Value() + Lib.GenC.Hidden();
                            }
                            """
        }, projectReferences: ["Lib"]);

        var libCsproj = Path.Combine(TestDir, "src", "Lib", "Lib.csproj");
        var csprojText = File.ReadAllText(libCsproj).Replace(
            "</Project>",
            """
              <ItemGroup>
                <Compile Include="obj/GenC.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(libCsproj, csprojText);

        var libObjDir = Path.Combine(TestDir, "src", "Lib", "obj");
        Directory.CreateDirectory(libObjDir);
        File.WriteAllText(Path.Combine(libObjDir, "GenC.cs"), """
                                                             namespace Lib;

                                                             public static class GenC
                                                             {
                                                                 public static int Hidden() => 3;
                                                             }
                                                             """);
        RestoreSolution();

        using var store = OpenStore(DbPath);
        var sink = new CapturingOutputSink();
        await IndexRunner.RunAsync(store, SolutionPath, [], null, "full", false, sink, false, false, CancellationToken.None);

        var snapshotId = store.LoadLatestSnapshot()!.SnapshotId;
        var docPaths = store.GetDocumentVersionIdsByPath(snapshotId).Keys;
        Assert.Contains("src/Lib/Secret.cs", docPaths);
        Assert.DoesNotContain("src/Lib/obj/GenC.cs", docPaths);

        var symbolIds = store.GetSymbolIdsInSnapshot(snapshotId);
        var secretTypeId = Assert.Single(symbolIds, id => id.StartsWith("T:Lib.Secret|", StringComparison.Ordinal));
        var secretMethodId = Assert.Single(symbolIds, id => id.StartsWith("M:Lib.Secret.Value|", StringComparison.Ordinal));
        var runMethodId = Assert.Single(symbolIds, id => id.StartsWith("M:App.Caller.Run|", StringComparison.Ordinal));
        Assert.NotNull(secretTypeId);

        var outgoing = store.GetOutgoingEdges(snapshotId, runMethodId);
        Assert.Contains(outgoing, e => e.Kind == "Calls" && e.TargetSymbolId == secretMethodId);

        var libIncompleteness = store.GetBindingIncompleteness(snapshotId, "Lib");
        var excludedRow = Assert.Single(libIncompleteness, r => r.Reason == "excluded_document");
        Assert.Equal("src/Lib/obj/GenC.cs", excludedRow.DocumentPath);
        Assert.Equal(1, excludedRow.Count);

        var output = sink.Output.ToString();
        var dropLine = Assert.Single(output.Split('\n'), line => line.Contains("edges_dropped_outside_snapshot_scope:", StringComparison.Ordinal));
        // Three edges touch Lib.GenC / Lib.GenC.Hidden: App.Caller.Run -> M:Lib.GenC.Hidden (Calls),
        // App.Caller.Run -> T:Lib.GenC (static-receiver References), and T:Lib.GenC -> M:Lib.GenC.Hidden (Declares).
        Assert.Contains("excluded_document=3", dropLine, StringComparison.Ordinal);
        Assert.Contains("other=0", dropLine, StringComparison.Ordinal);
        Assert.DoesNotContain(sink.ErrorLines, line => line.Contains("edges_dropped_reason_other", StringComparison.Ordinal));
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
