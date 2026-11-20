using System.Globalization;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Mcp;
using Lurp.Mcp.Tools;
using Lurp.Storage;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;

namespace Lurp.Tests;

public sealed class SymbolResolverTests : IntegrationTestBase
{
    private const string Project = "Res";

    private const string ShapesSource = """
        namespace Ns;
        public class Foo { public void Do(int a) { } public void Do(string a) { } }
        public class FooBar { }
        public class My_Type { }
        public class MyXType { }
        public class Repo<T> { public void Add(T item) { } }
        public class Repo<T1, T2> { }
        public class Plain { public void Go() { } }
        """;

    private const string GenSource = """
        namespace Ns;
        public class GenOnly { }
        """;

    private async Task<(string SnapshotId, string AssemblyIdentity)> SeedAndIndexAsync()
    {
        CreateProject(Project, new Dictionary<string, string>
        {
            ["Shapes.cs"] = ShapesSource,
            ["Gen.g.cs"] = GenSource
        });
        var snapshotId = await RunFullIndexAsync(DbPath);

        using var store = OpenStore(DbPath);
        var plain = Assert.Single(store.FindSymbols(SymbolKey.DocCommentId, "T:Ns.Plain", snapshotId));
        return (snapshotId, plain.SymbolId.AssemblyIdentity);
    }

    private static string NotFoundMessage(string input, string snapshotId) =>
        $"ERROR: Symbol '{input}' not found in snapshot '{snapshotId}'. {HandlerBootstrap.AcceptedSymbolFormsText}";

    private static string AmbiguousMessage(string input, string snapshotId, params string[] ids)
    {
        var ordered = ids.OrderBy(static id => id, StringComparer.Ordinal);
        return $"ERROR: Symbol '{input}' matches {ids.Length.ToString(CultureInfo.InvariantCulture)} symbols in snapshot '{snapshotId}'. Pass one of these ids: {string.Join(", ", ordered)}.";
    }

    private CapturingOutputSink RunFindSymbol(string symbol, bool includeGenerated = false)
    {
        var args = new List<string>
        {
            "--mode=find-symbol",
            $"--symbol={symbol}",
            $"--output-dir={TestDir}"
        };
        if (includeGenerated)
            args.Add("--include-generated");
        return CapturingOutputSink.Capture(sink => FindSymbolHandler.Run([.. args], sink));
    }

    private int CountAnnotations()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM annotations;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private long ReadAnnotationSequence()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seq FROM sqlite_sequence WHERE name = 'annotations';";
        var scalar = cmd.ExecuteScalar();
        return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Resolve_Prefix_IsNotFound()
    {
        var (snapshotId, _) = await SeedAndIndexAsync();
        var result = RunFindSymbol("Ns.Fo");

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage("Ns.Fo", snapshotId), result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_WildcardCharacter_IsNotFound()
    {
        var (snapshotId, _) = await SeedAndIndexAsync();
        var result = RunFindSymbol("Ns.My_Typ");

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage("Ns.My_Typ", snapshotId), result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_CaseVariant_IsNotFound()
    {
        var (snapshotId, _) = await SeedAndIndexAsync();
        var result = RunFindSymbol("ns.foobar");

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage("ns.foobar", snapshotId), result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_Overloads_IsAmbiguous()
    {
        var (snapshotId, asm) = await SeedAndIndexAsync();
        var result = RunFindSymbol("Ns.Foo.Do");

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(
            AmbiguousMessage("Ns.Foo.Do", snapshotId,
                $"M:Ns.Foo.Do(System.Int32)|{asm}",
                $"M:Ns.Foo.Do(System.String)|{asm}"),
            result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_GenericNameWithoutParameters_TwoArities_IsAmbiguous()
    {
        var (snapshotId, asm) = await SeedAndIndexAsync();
        var result = RunFindSymbol("Ns.Repo");

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(
            AmbiguousMessage("Ns.Repo", snapshotId,
                $"T:Ns.Repo`1|{asm}",
                $"T:Ns.Repo`2|{asm}"),
            result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_MemberOfGenericType_ByBareForm()
    {
        var (_, asm) = await SeedAndIndexAsync();
        var result = RunFindSymbol("Ns.Repo.Add");

        Assert.Null(result.Failure);
        using var doc = JsonDocument.Parse(result.Stdout);
        Assert.Equal($"M:Ns.Repo`1.Add(`0)|{asm}", doc.RootElement.GetProperty("symbol_id").GetString());
    }

    [Fact]
    public async Task Resolve_GenericName_WithParameters_ByFqn()
    {
        var (_, asm) = await SeedAndIndexAsync();
        var expected = $"T:Ns.Repo`1|{asm}";

        foreach (var input in new[] { "Ns.Repo<T>", "global::Ns.Repo<T>" })
        {
            var result = RunFindSymbol(input);
            Assert.Null(result.Failure);
            using var doc = JsonDocument.Parse(result.Stdout);
            Assert.Equal(expected, doc.RootElement.GetProperty("symbol_id").GetString());
        }
    }

    [Fact]
    public async Task Resolve_FullIdWithWrongAssembly_IsNotFound()
    {
        var (snapshotId, _) = await SeedAndIndexAsync();
        var result = RunFindSymbol("T:Ns.Plain|Wrong");

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage("T:Ns.Plain|Wrong", snapshotId), result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_DanglingFullId_IsNotFound()
    {
        var (snapshotId, asm) = await SeedAndIndexAsync();
        var input = $"T:Ns.DoesNotExist|{asm}";

        var result = CapturingOutputSink.Capture(sink => AnnotationHandler.RunAnnotate([
            $"--symbol={input}",
            "--annotation-kind=note",
            "--value=x",
            $"--output-dir={TestDir}"
        ], sink));

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage(input, snapshotId), result.Failure.Message);
        Assert.Equal(0, CountAnnotations());
    }

    [Fact]
    public async Task Annotate_PrefixName_WritesNothing()
    {
        var (snapshotId, _) = await SeedAndIndexAsync();

        var result = CapturingOutputSink.Capture(sink => AnnotationHandler.RunAnnotate([
            "--symbol=Ns.Fo",
            "--annotation-kind=note",
            "--value=x",
            $"--output-dir={TestDir}"
        ], sink));

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage("Ns.Fo", snapshotId), result.Failure.Message);
        Assert.Equal(0, CountAnnotations());
    }

    [Fact]
    public async Task Impact_Overloads_IsAmbiguous()
    {
        var (snapshotId, asm) = await SeedAndIndexAsync();

        var result = CapturingOutputSink.Capture(sink => ImpactHandler.Run([
            "--mode=impact",
            "--symbol=Ns.Foo.Do",
            $"--output-dir={TestDir}"
        ], sink));

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal(
            AmbiguousMessage("Ns.Foo.Do", snapshotId,
                $"M:Ns.Foo.Do(System.Int32)|{asm}",
                $"M:Ns.Foo.Do(System.String)|{asm}"),
            result.Failure.Message);
    }

    [Fact]
    public async Task Resolve_GeneratedOnly_NeedsTheFlag()
    {
        var (snapshotId, asm) = await SeedAndIndexAsync();
        var expected = $"T:Ns.GenOnly|{asm}";

        var withoutFlag = RunFindSymbol("Ns.GenOnly", includeGenerated: false);
        Assert.NotNull(withoutFlag.Failure);
        Assert.Equal(1, withoutFlag.Failure!.ExitCode);
        Assert.Equal(NotFoundMessage("Ns.GenOnly", snapshotId), withoutFlag.Failure.Message);

        var withFlag = RunFindSymbol("Ns.GenOnly", includeGenerated: true);
        Assert.Null(withFlag.Failure);
        using (var doc = JsonDocument.Parse(withFlag.Stdout))
            Assert.Equal(expected, doc.RootElement.GetProperty("symbol_id").GetString());

        var annotateResult = CapturingOutputSink.Capture(sink => AnnotationHandler.RunAnnotate([
            "--symbol=Ns.GenOnly",
            "--annotation-kind=note",
            "--value=on-generated",
            $"--output-dir={TestDir}"
        ], sink));
        Assert.Null(annotateResult.Failure);
        using (var doc = JsonDocument.Parse(annotateResult.Stdout))
            Assert.Equal(expected, doc.RootElement.GetProperty("symbol_id").GetString());
    }

    [Fact]
    public async Task Resolve_McpAndCli_SameText()
    {
        await SeedAndIndexAsync();
        var cliResult = RunFindSymbol("Ns.Foo.Do");
        Assert.NotNull(cliResult.Failure);

        await using var session = McpSessionContext.Create([$"--solution={SolutionPath}", $"--output-dir={TestDir}"]);
        var tool = new FindSymbolTool(session);
        var ex = Assert.Throws<McpProtocolException>(() => tool.LurpFindSymbol(symbol: "Ns.Foo.Do"));

        Assert.Equal(McpErrorCode.InvalidParams, ex.ErrorCode);
        Assert.Equal("ERROR: " + ex.Message, cliResult.Failure!.Message);
    }

    [Fact]
    public async Task Annotate_ReturnsTheInsertedRowId()
    {
        await SeedAndIndexAsync();

        var expectedFirst = ReadAnnotationSequence() + 1;
        var first = CapturingOutputSink.Capture(sink => AnnotationHandler.RunAnnotate([
            "--symbol=Ns.Plain",
            "--annotation-kind=note",
            "--value=dup",
            $"--output-dir={TestDir}"
        ], sink));
        Assert.Null(first.Failure);
        long id1;
        using (var doc = JsonDocument.Parse(first.Stdout))
            id1 = doc.RootElement.GetProperty("annotation_id").GetInt64();
        Assert.Equal(expectedFirst, id1);

        var expectedSecond = ReadAnnotationSequence() + 1;
        var second = CapturingOutputSink.Capture(sink => AnnotationHandler.RunAnnotate([
            "--symbol=Ns.Plain",
            "--annotation-kind=note",
            "--value=dup",
            $"--output-dir={TestDir}"
        ], sink));
        Assert.Null(second.Failure);
        long id2;
        using (var doc = JsonDocument.Parse(second.Stdout))
            id2 = doc.RootElement.GetProperty("annotation_id").GetInt64();
        Assert.Equal(expectedSecond, id2);
        Assert.NotEqual(id1, id2);

        foreach (var id in new[] { id1, id2 })
        {
            var retract = CapturingOutputSink.Capture(sink => AnnotationHandler.RunRetractAnnotation([
                $"--annotation-id={id.ToString(CultureInfo.InvariantCulture)}",
                $"--output-dir={TestDir}"
            ], sink));
            Assert.Null(retract.Failure);
        }
    }
}
