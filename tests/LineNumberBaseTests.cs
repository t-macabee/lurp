using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

/// <summary>
/// Audit §9 invariant T4 closes: emitted line numbers are 1-based, matching the
/// --line= input convention. Storage stays Roslyn-native 0-based (Option A);
/// conversion happens ONLY at the emit boundary, routed through the single
/// LineNumbers choke point (src/Storage/LineNumbers.cs). The fixture's physical
/// line numbers are ground truth, counted 1-based in the literal below.
/// </summary>
public sealed class LineNumberBaseTests : IntegrationTestBase
{
    private const string Project = "Core";
    private const string Document = "src/Core/Widget.cs";
    private const string CallerRunFqn = "global::Core.Caller.Run";
    private const string HelperFqn = "global::Core.Helper";
    private const string HelperDoFqn = "global::Core.Helper.Do";

    // Ground truth (1-based physical lines):
    //  1: namespace Core;
    //  2: (blank)
    //  3: public class Caller
    //  4: {
    //  5:     public void Run()                 <- declaration (leading trivia = indent on this line)
    //  6:     {
    //  7:         var helper = new Helper();
    //  8:         helper.Do();                 <- Calls edge call site
    //  9:     }
    // 10: }
    // 11: (blank)                            <- leading trivia of class Helper (FullSpan start)
    // 12: public class Helper
    // 13: {
    // 14:     public void Do()               <- declaration (round-trip anchor)
    // 15:     {
    // 16:     }
    // 17: }                                 <- class closes here (declaration end)
    private static readonly string Source = """
        namespace Core;

        public class Caller
        {
            public void Run()
            {
                var helper = new Helper();
                helper.Do();
            }
        }

        public class Helper
        {
            public void Do()
            {
            }
        }
        """;

    private async Task<string> SeedAndIndexAsync()
    {
        CreateProject(Project, new Dictionary<string, string> { ["Widget.cs"] = Source });
        return await RunFullIndexAsync(DbPath);
    }

    private sealed record RunResult(string Stdout, CliExitException? Failure);

    /// <summary>Runs a handler in-process with stdout captured; a
    /// <see cref="CliExitException"/> (the only exit mechanism a handler uses) is
    /// returned in the result rather than thrown.</summary>
    private static RunResult RunCaptured(Action action)
    {
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            action();
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return new RunResult(stdout.ToString(), failure);
    }

    /// <summary>Edge output is 1-based while storage stays 0-based. The audit
    /// confirmed the pre-fix off-by-one empirically (reported L18/26/34/45/53,
    /// actual 19/27/35/46/54); this test pins both sides of the boundary.</summary>
    [Fact]
    public async Task CallsEdge_CallSiteOnPhysicalLine8_IsOneBasedOnOutput_ZeroBasedInStorage()
    {
        var snapshotId = await SeedAndIndexAsync();
        var runId = ResolveSymbolId(snapshotId, CallerRunFqn);
        var doId = ResolveSymbolId(snapshotId, HelperDoFqn);

        using var store = OpenStore(DbPath);

        // Option A: the persisted edge keeps the Roslyn-native 0-based line (7).
        var storageEdge = store.GetEdges(snapshotId)
            .Single(e => e.Kind == "Calls" && e.SourceSymbolId == runId && e.TargetSymbolId == doId);
        Assert.Equal(7, storageEdge.SourceStartLine);
        Assert.Equal(7, storageEdge.SourceEndLine);

        // Output: the impact hop reports the call site's physical line 8.
        var traverser = new ImpactTraverser(store, snapshotId, store);
        var hop = traverser.TraceImpact(runId, ImpactDirection.Downstream)
            .SelectMany(static path => path.Hops)
            .Single(h => h.EdgeKind == "Calls" && h.TargetSymbolId == doId);
        Assert.Equal(8, hop.SourceLine);
        Assert.Equal(8, hop.SourceEndLine);
    }

    /// <summary>Declaration locations report the fixture's physical 1-based lines.
    /// An indented method's FullSpan leading trivia is the indent on its own line,
    /// so its start_line equals the code line (Run -> 5). A class preceded by a
    /// blank line has leading trivia starting on that blank line (Helper -> 11),
    /// while its end_line is the line of its closing brace (17).</summary>
    [Fact]
    public async Task DeclarationLocation_ReportsOneBasedPhysicalLines()
    {
        var snapshotId = await SeedAndIndexAsync();
        var helperId = ResolveSymbolId(snapshotId, HelperFqn);
        var runId = ResolveSymbolId(snapshotId, CallerRunFqn);

        using var store = OpenStore(DbPath);

        var helperLoc = Assert.Single(store.GetDeclarationLocations(helperId, snapshotId));
        Assert.Equal(11, helperLoc.StartLine); // FullSpan includes leading trivia: the blank line 11
        Assert.Equal(17, helperLoc.EndLine);   // class closes on physical line 17

        var runLoc = Assert.Single(store.GetDeclarationLocations(runId, snapshotId));
        Assert.Equal(5, runLoc.StartLine);     // leading trivia is only the indent on line 5
    }

    /// <summary>Round-trip: the reported start_line of a declaration feeds
    /// verbatim into navigate --line= and resolves to the same symbol. This is the
    /// property that matters to an agent and failed before T4 (navigate converted
    /// 0-based output back to a 0-based index and landed one line early).</summary>
    [Fact]
    public async Task ReportedStartLine_RoundTripsThroughNavigate_ResolvesSameSymbol()
    {
        var snapshotId = await SeedAndIndexAsync();
        var doId = ResolveSymbolId(snapshotId, HelperDoFqn);

        string reportedStartLine;
        using (var store = OpenStore(DbPath))
        {
            var loc = Assert.Single(store.GetDeclarationLocations(doId, snapshotId));
            Assert.Equal(14, loc.StartLine);
            reportedStartLine = loc.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var result = RunCaptured(() => NavigateHandler.Run(new[]
        {
            "--mode=navigate",
            $"--file={Document}",
            $"--line={reportedStartLine}",
            $"--output-dir={TestDir}",
        }));

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        var target = doc.RootElement.GetProperty("target");
        Assert.Equal(doId, target.GetProperty("symbol_id").GetString());
        Assert.Equal(Document, target.GetProperty("document_path").GetString());
    }
}
