using System.Text.Json;
using Lurp.Handlers;

namespace Lurp.Tests;

public sealed class SearchKindTests : IntegrationTestBase
{
    private async Task IndexKindFixtureAsync()
    {
        CreateProject("KindProj", new Dictionary<string, string>
        {
            ["Calc.cs"] = "namespace KindProj; public static class Calc { public static int Add(int a) => a; }"
        });
        await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task Search_KindIsCaseInsensitive()
    {
        await IndexKindFixtureAsync();
        var stdout = await CliModeRunner.RunAsync("search", [
            $"--output-dir={Path.GetDirectoryName(DbPath)!}",
            "--type=symbol",
            "--query=Add",
            "--kind=method"
        ]);

        using var doc = JsonDocument.Parse(stdout);
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        var item = results[0];
        Assert.Equal("global::KindProj.Calc.Add", item.GetProperty("fully_qualified_name").GetString());
        Assert.Equal("Method", item.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Search_UnknownKind_FailsAndListsTheKinds()
    {
        await IndexKindFixtureAsync();
        var entry = Program.FindMode("search")!;
        var sink = new CapturingOutputSink();
        string[] args =
        [
            $"--output-dir={Path.GetDirectoryName(DbPath)!}",
            "--query=Add",
            "--kind=NamedType"
        ];

        var ex = await Assert.ThrowsAsync<CliExitException>(async () =>
        {
            CliFlagValidation.Validate(entry, args);
            await entry.Handler(args, sink);
        });

        Assert.Equal(1, ex.ExitCode);
        Assert.Equal("ERROR: --kind must be one of: Type, Method, Property, Field, Event (case-insensitive). Got 'NamedType'.", ex.Message);
    }
}
