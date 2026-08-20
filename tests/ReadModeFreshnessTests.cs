using Lurp.Handlers;
using Lurp.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.Tests;

/// <summary>
///     The audit §9 invariant T1 closes: every read mode signals freshness, and
///     <c>--require-fresh</c> refuses a stale read. The raw-source modes
///     (<c>get-source</c>, the five source views of <c>get-symbol</c>) have no JSON
///     envelope, so their freshness travels on stderr plus the exit code only; the
///     JSON-envelope modes (<c>navigate</c>, <c>get-symbol --view=metadata</c>)
///     additionally embed the block in the payload.
/// </summary>
public sealed class ReadModeFreshnessTests : IntegrationTestBase
{
    private const string Project = "Core";
    private const string Document = "src/Core/Widget.cs";
    private const string SymbolFqn = "global::Core.Widget";

    private const string OriginalSource = """
                                                    namespace Core;

                                                    public class Widget
                                                    {
                                                        public int Value { get; set; }

                                                        public void DoWork()
                                                        {
                                                        }
                                                    }
                                                    """;

    private const string MutatedSource = """
                                                   namespace Core;

                                                   public class Widget
                                                   {
                                                       public int Value { get; set; }

                                                       public void DoWork()
                                                       {
                                                           System.Console.WriteLine("mutated");
                                                       }
                                                   }
                                                   """;

    private void Seed()
    {
        CreateProject(Project, new Dictionary<string, string> { ["Widget.cs"] = OriginalSource });
    }

    /// <summary>
    ///     Mutates the fixture file on disk without re-indexing, then pins its
    ///     mtime strictly after the snapshot's build time so the stat-only freshness
    ///     check deterministically reports it as changed regardless of filesystem
    ///     timestamp granularity.
    /// </summary>
    private void MutateWidget()
    {
        WriteFile(Project, "Widget.cs", MutatedSource);
        File.SetLastWriteTimeUtc(Path.Combine(TestDir, "src", Project, "Widget.cs"), DateTime.UtcNow.AddSeconds(10));
    }

    private string[] HandlerArgs(string mode, string[] modeArgs, bool quiet, bool requireFresh)
    {
        var list = new List<string> { "--mode=" + mode, "--output-dir=" + TestDir };
        list.AddRange(modeArgs);
        if (quiet)
            list.Add("--quiet");
        if (requireFresh)
            list.Add("--require-fresh");
        return [.. list];
    }

    /// <summary>
    ///     Runs a handler in-process with stdout/stderr captured. A
    ///     <see cref="CliExitException" /> (the only exit mechanism a handler uses) is
    ///     returned in the result rather than thrown, so tests can assert exit code 2.
    /// </summary>
    private static RunResult RunCaptured(Action action)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            action();
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return new RunResult(stdout.ToString(), stderr.ToString(), failure);
    }

    private static string FreshnessState(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("freshness").GetProperty("state").GetString()!;
    }

    /// <summary>
    ///     The payload minus the <c>freshness</c> block. <c>checked_at_utc</c>
    ///     changes on every call by design, so byte comparison only makes sense for the
    ///     non-freshness fields.
    /// </summary>
    private static string PayloadWithoutFreshness(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var node = JsonNode.Parse(doc.RootElement.GetRawText())!;
        node.AsObject().Remove("freshness");
        return node.ToJsonString();
    }

    private string ReadIndexedDocument(string snapshotId)
    {
        using var store = OpenStore(DbPath);
        return store.GetSource(Document, snapshotId)!;
    }

    private string ReadIndexedDeclarationSource(string symbolId, string snapshotId)
    {
        using var store = OpenStore(DbPath);
        return store.GetSymbolSource(symbolId, snapshotId, ViewKind.Declaration)!;
    }

    // ---- Registry invariant (the durable one) ----

    [Fact]
    public void EveryReadMode_Registry_DeclaresFreshnessFlags()
    {
        var required = new[] { "search", "grep", "find-symbol", "impact", "context", "get-source", "get-symbol", "navigate", "outline", "diagnostics" };
        // The audit names exactly five exempt modes. get-annotations is a read mode
        // outside T1's scope, so it is exempted explicitly here too; the partition
        // assertion below forces any brand-new mode to be classified one way or the
        // other, so a new read mode added without freshness fails the build.
        var exempt = new[] { "index", "annotate", "timings", "diff", "status", "get-annotations", "serve" };

        var registryNames = Program.ModeRegistry.Select(e => e.Name).ToArray();
        var classified = required.Concat(exempt).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(registryNames.OrderBy(n => n, StringComparer.Ordinal), classified);

        foreach (var mode in required)
        {
            var entry = Program.ModeRegistry.Single(e => e.Name == mode);
            Assert.Contains("--freshness=", entry.Flags);
            Assert.Contains("--require-fresh", entry.Flags);
            Assert.Contains("--quiet", entry.Flags);
        }
    }

    // ---- Behavioral, per mode ----

    [Fact]
    public async Task GetSource_StaleSource_ReportsStaleAndHonorsRequireFresh()
    {
        Seed();
        var snapshotId = await RunFullIndexAsync(DbPath);
        var baseline = ReadIndexedDocument(snapshotId);

        var fresh = RunCaptured(() => GetSourceHandler.Run(Args(false, false)));
        Assert.Contains("freshness: state=fresh", fresh.Stderr);
        Assert.Equal(baseline, fresh.Stdout);

        MutateWidget();

        var stale = RunCaptured(() => GetSourceHandler.Run(Args(false, false)));
        Assert.Contains("freshness: state=stale", stale.Stderr);
        Assert.Equal(baseline, stale.Stdout); // the fix labels staleness; it does not re-serve

        var quietStale = RunCaptured(() => GetSourceHandler.Run(Args(true, false)));
        Assert.DoesNotContain("freshness:", quietStale.Stderr);
        Assert.Equal(baseline, quietStale.Stdout);

        var rejected = RunCaptured(() => GetSourceHandler.Run(Args(false, true)));
        Assert.NotNull(rejected.Failure);
        Assert.Equal(2, rejected.Failure!.ExitCode);
        Assert.Equal(string.Empty, rejected.Stdout); // no stale bytes emitted

        var quietRejected = RunCaptured(() => GetSourceHandler.Run(Args(true, true)));
        Assert.NotNull(quietRejected.Failure);
        Assert.Equal(2, quietRejected.Failure!.ExitCode);
        Assert.DoesNotContain("freshness:", quietRejected.Stderr);
        Assert.Equal(string.Empty, quietRejected.Stdout);

        string[] Args(bool quiet, bool requireFresh)
        {
            return HandlerArgs("get-source", [$"--document={Document}"], quiet, requireFresh);
        }
    }

    [Fact]
    public async Task GetSymbol_MetadataAndSourceViews_ReportStaleAndHonorRequireFresh()
    {
        Seed();
        var snapshotId = await RunFullIndexAsync(DbPath);
        var symbolId = ResolveSymbolId(snapshotId, SymbolFqn);

        var freshMeta = RunCaptured(() => GetSymbolHandler.Run(Args(false, false, "metadata")));
        Assert.Contains("freshness: state=fresh", freshMeta.Stderr);
        Assert.Equal("fresh", FreshnessState(freshMeta.Stdout));

        MutateWidget();

        var staleMeta = RunCaptured(() => GetSymbolHandler.Run(Args(false, false, "metadata")));
        Assert.Contains("freshness: state=stale", staleMeta.Stderr);
        Assert.Equal("stale", FreshnessState(staleMeta.Stdout));
        Assert.Equal(PayloadWithoutFreshness(freshMeta.Stdout), PayloadWithoutFreshness(staleMeta.Stdout));

        // The five raw-source views are byte-exact by contract: same indexed bytes,
        // staleness travels on stderr plus the exit code only.
        var baselineDeclaration = ReadIndexedDeclarationSource(symbolId, snapshotId);
        var staleSource = RunCaptured(() => GetSymbolHandler.Run(Args(false, false, "declaration")));
        Assert.Contains("freshness: state=stale", staleSource.Stderr);
        Assert.Equal(baselineDeclaration, staleSource.Stdout);

        var rejected = RunCaptured(() => GetSymbolHandler.Run(Args(false, true, "metadata")));
        Assert.NotNull(rejected.Failure);
        Assert.Equal(2, rejected.Failure!.ExitCode);
        Assert.Equal(string.Empty, rejected.Stdout);

        var quietRejected = RunCaptured(() => GetSymbolHandler.Run(Args(true, true, "metadata")));
        Assert.NotNull(quietRejected.Failure);
        Assert.Equal(2, quietRejected.Failure!.ExitCode);
        Assert.DoesNotContain("freshness:", quietRejected.Stderr);
        Assert.Equal(string.Empty, quietRejected.Stdout);

        string[] Args(bool quiet, bool requireFresh, string view)
        {
            return HandlerArgs("get-symbol", [$"--symbol={symbolId}", $"--view={view}"], quiet, requireFresh);
        }
    }

    [Fact]
    public async Task Navigate_StaleSource_ReportsStaleAndHonorsRequireFresh()
    {
        Seed();
        await RunFullIndexAsync(DbPath); // freshness is checked against the latest snapshot the handler resolves

        var fresh = RunCaptured(() => NavigateHandler.Run(Args(false, false)));
        Assert.Contains("freshness: state=fresh", fresh.Stderr);
        Assert.Equal("fresh", FreshnessState(fresh.Stdout));

        MutateWidget();

        var stale = RunCaptured(() => NavigateHandler.Run(Args(false, false)));
        Assert.Contains("freshness: state=stale", stale.Stderr);
        Assert.Equal("stale", FreshnessState(stale.Stdout));
        Assert.Equal(PayloadWithoutFreshness(fresh.Stdout), PayloadWithoutFreshness(stale.Stdout));

        var rejected = RunCaptured(() => NavigateHandler.Run(Args(false, true)));
        Assert.NotNull(rejected.Failure);
        Assert.Equal(2, rejected.Failure!.ExitCode);
        Assert.Equal(string.Empty, rejected.Stdout);

        var quietRejected = RunCaptured(() => NavigateHandler.Run(Args(true, true)));
        Assert.NotNull(quietRejected.Failure);
        Assert.Equal(2, quietRejected.Failure!.ExitCode);
        Assert.DoesNotContain("freshness:", quietRejected.Stderr);
        Assert.Equal(string.Empty, quietRejected.Stdout);

        string[] Args(bool quiet, bool requireFresh)
        {
            return HandlerArgs("navigate", [$"--file={Document}", "--line=3"], quiet, requireFresh);
        }
    }

    private sealed record RunResult(string Stdout, string Stderr, CliExitException? Failure);
}