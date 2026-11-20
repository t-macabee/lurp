using Lurp.Handlers;

namespace Lurp.Tests;

public sealed class GetSourceWindowTests : IntegrationTestBase
{
    private const string Document = "src/WinProj/Five.cs";
    private const string MissingDocument = "src/WinProj/Missing.cs";

    private async Task<string> IndexFiveLinesAsync()
    {
        CreateProject("WinProj", new Dictionary<string, string>
        {
            ["Five.cs"] = "// l1\n// l2\n// l3\n// l4\n// l5\n"
        });
        return await RunFullIndexAsync(DbPath);
    }

    [Fact]
    public async Task GetSource_StartLineBeyondTheEnd_NamesTheFlag()
    {
        await IndexFiveLinesAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;

        var result = await CapturingOutputSink.CaptureAsync(sink =>
        {
            GetSourceHandler.Run([
                $"--output-dir={outputDir}",
                $"--document={Document}",
                "--start-line=99"
            ], sink);
            return Task.CompletedTask;
        });

        Assert.NotNull(result.Failure);
        Assert.Equal(1, result.Failure!.ExitCode);
        Assert.Equal($"ERROR: --start-line=99 is beyond the last line (6) of '{Document}'.", result.Failure.Message);
    }

    [Theory]
    [InlineData("get-source")]
    [InlineData("outline")]
    [InlineData("get-annotations")]
    public async Task DocumentNotFound_HasOneTextInEveryReader(string mode)
    {
        var snapshotId = await IndexFiveLinesAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;
        var entry = Program.FindMode(mode)!;
        var sink = new CapturingOutputSink();

        var ex = await Assert.ThrowsAsync<CliExitException>(() => entry.Handler([
            $"--output-dir={outputDir}",
            $"--document={MissingDocument}"
        ], sink));

        Assert.Equal(1, ex.ExitCode);
        Assert.Equal($"ERROR: Document '{MissingDocument}' not found in snapshot '{snapshotId}'.", ex.Message);
    }

    [Fact]
    public async Task GetSource_LineWindows()
    {
        await IndexFiveLinesAsync();
        var outputDir = Path.GetDirectoryName(DbPath)!;

        var slice23 = await CliModeRunner.RunAsync("get-source", [
            $"--output-dir={outputDir}",
            $"--document={Document}",
            "--start-line=2",
            "--end-line=3"
        ]);
        Assert.Equal("// l2\n// l3\n", slice23);

        var sliceCtx = await CliModeRunner.RunAsync("get-source", [
            $"--output-dir={outputDir}",
            $"--document={Document}",
            "--start-line=3",
            "--context-lines=1"
        ]);
        Assert.Equal("// l2\n// l3\n// l4\n// l5\n", sliceCtx);

        var sliceCut = await CliModeRunner.RunAsync("get-source", [
            $"--output-dir={outputDir}",
            $"--document={Document}",
            "--start-line=5",
            "--end-line=99"
        ]);
        Assert.Equal("// l5\n", sliceCut);

        var ctxOnly = CapturingOutputSink.Capture(sink => GetSourceHandler.Run([
            $"--output-dir={outputDir}",
            $"--document={Document}",
            "--context-lines=1"
        ], sink));
        Assert.NotNull(ctxOnly.Failure);
        Assert.Equal(1, ctxOnly.Failure!.ExitCode);
        Assert.Equal("ERROR: --context-lines requires --start-line or --end-line.", ctxOnly.Failure.Message);

        var badRange = CapturingOutputSink.Capture(sink => GetSourceHandler.Run([
            $"--output-dir={outputDir}",
            $"--document={Document}",
            "--start-line=3",
            "--end-line=2"
        ], sink));
        Assert.NotNull(badRange.Failure);
        Assert.Equal(1, badRange.Failure!.ExitCode);
        Assert.Equal("ERROR: --start-line must be <= --end-line.", badRange.Failure.Message);

        var badStart = CapturingOutputSink.Capture(sink => GetSourceHandler.Run([
            $"--output-dir={outputDir}",
            $"--document={Document}",
            "--start-line=0"
        ], sink));
        Assert.NotNull(badStart.Failure);
        Assert.Equal(1, badStart.Failure!.ExitCode);
        Assert.Equal("ERROR: --start-line must be a positive integer.", badStart.Failure.Message);
    }
}
