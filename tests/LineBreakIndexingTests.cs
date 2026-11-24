using System.Text.Json;

namespace Lurp.Tests;

public sealed class LineBreakIndexingTests : IntegrationTestBase
{
    private async Task IndexCrProjectAsync()
    {
        CreateProject("CrProj", new Dictionary<string, string>
        {
            ["Cr.cs"] = "namespace CrProj;\rpublic class CrA { }\rpublic class CrB { void M() { int unused = 1; } }\r",
            ["Ls.cs"] = "// a\u2028// b\npublic class L { }\n",
            ["End.cs"] = "namespace CrProj;\npublic class A { }\n"
        });
        await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task CrOnlyFile_HasOneLineNumberInEveryMode()
    {
        await IndexCrProjectAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;

        // 1. Diagnostics (Roslyn oracle): CS0219 at line 3
        var diagJson = await CliModeRunner.RunAsync("diagnostics", [
            $"--output-dir={outputDir}",
            "--document=src/CrProj/Cr.cs",
            "--id=CS0219"
        ]);
        using (var diagDoc = JsonDocument.Parse(diagJson))
        {
            var diag = Assert.Single(diagDoc.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(3, diag.GetProperty("start_line").GetInt32());
        }

        // 2. Outline: CrB start_line == 3
        var outlineJson = await CliModeRunner.RunAsync("outline", [
            $"--output-dir={outputDir}",
            "--document=src/CrProj/Cr.cs"
        ]);
        using (var outlineDoc = JsonDocument.Parse(outlineJson))
        {
            var crB = outlineDoc.RootElement.GetProperty("declarations").EnumerateArray()
                .Single(d => d.GetProperty("fully_qualified_name").GetString() == "global::CrProj.CrB");
            Assert.Equal(3, crB.GetProperty("start_line").GetInt32());
        }

        // 3. Grep: "class CrB" at line 3, column 7
        var grepJson = await CliModeRunner.RunAsync("grep", [
            $"--output-dir={outputDir}",
            "--query=class CrB"
        ]);
        using (var grepDoc = JsonDocument.Parse(grepJson))
        {
            var match = Assert.Single(grepDoc.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal("src/CrProj/Cr.cs", match.GetProperty("document_path").GetString());
            Assert.Equal(3, match.GetProperty("start_line").GetInt32());
            Assert.Equal(7, match.GetProperty("start_column").GetInt32());
        }

        // 4. get-source --start-line=3 --end-line=3
        var sourceSlice = await CliModeRunner.RunAsync("get-source", [
            $"--output-dir={outputDir}",
            "--document=src/CrProj/Cr.cs",
            "--start-line=3",
            "--end-line=3"
        ]);
        Assert.Equal("public class CrB { void M() { int unused = 1; } }\r", sourceSlice);

        // 5. Document has 4 lines (three breaks, the last at the end)
        using var store = OpenStore(DbPath);
        var snapshotId = store.GetLatestSnapshotId()!;
        var wholeSlice = store.GetSourceSlice("src/CrProj/Cr.cs", snapshotId, null, null, null);
        Assert.NotNull(wholeSlice);
        Assert.Equal(4, wholeSlice!.TotalLines);
    }

    [Fact]
    public async Task LineSeparatorInComment_StartsALine()
    {
        await IndexCrProjectAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;

        var grepJson = await CliModeRunner.RunAsync("grep", [
            $"--output-dir={outputDir}",
            "--query=class L"
        ]);
        using var grepDoc = JsonDocument.Parse(grepJson);
        var match = Assert.Single(grepDoc.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("src/CrProj/Ls.cs", match.GetProperty("document_path").GetString());
        Assert.Equal(3, match.GetProperty("start_line").GetInt32());
        Assert.Equal(7, match.GetProperty("start_column").GetInt32());
    }

    [Fact]
    public async Task DeclarationBeforeATrailingNewline_EndsOnTheDeclarationLine()
    {
        await IndexCrProjectAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;

        var findJson = await CliModeRunner.RunAsync("find-symbol", [
            $"--output-dir={outputDir}",
            "--symbol=CrProj.A"
        ]);
        using var findDoc = JsonDocument.Parse(findJson);
        var loc = Assert.Single(findDoc.RootElement.GetProperty("locations").EnumerateArray());
        Assert.Equal(2, loc.GetProperty("start_line").GetInt32());
        Assert.Equal(0, loc.GetProperty("start_column").GetInt32());
        Assert.Equal(2, loc.GetProperty("end_line").GetInt32());
        Assert.Equal("public class A { }".Length, loc.GetProperty("end_column").GetInt32());
    }
}
