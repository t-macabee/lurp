using System.Diagnostics;
using System.Text;
using Lurp.Parity.Shared;

namespace Lurp.Tests.Mcp;

public sealed class McpStdioClientTests
{
    private static string[] Pwsh(string script) =>
        ["pwsh", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))];

    [Fact]
    public async Task NonJsonStdoutLine_Throws()
    {
        using var server = McpServeProcess.Start(Pwsh("Write-Output 'not-json'; Start-Sleep 30"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => server.Client.SendAsync("ping", null, TimeSpan.FromSeconds(20)));
        Assert.Contains("not-json", exception.Message);
    }

    [Fact]
    public async Task NoResponse_ThrowsTimeoutException_AfterTheGivenTimeout()
    {
        using var server = McpServeProcess.Start(Pwsh("Start-Sleep 30"));

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(
            () => server.Client.SendAsync("ping", null, TimeSpan.FromMilliseconds(500)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"the given 500 ms timeout did not apply (took {stopwatch.ElapsedMilliseconds} ms).");
    }

    [Fact]
    public async Task Observer_SeesEveryLine_CloseReturnsExitCode_StderrIsCaptured()
    {
        var script =
            "Write-Output '{\"jsonrpc\":\"2.0\",\"method\":\"note\"}'; " +
            "Write-Output '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'; " +
            "[Console]::Error.WriteLine('err-line'); " +
            "$null = [Console]::In.ReadToEnd(); exit 3";
        var observed = new List<string>();
        using var server = McpServeProcess.Start(
            Pwsh(script),
            line =>
            {
                lock (observed)
                    observed.Add(line);
            });

        var response = await server.Client.SendAsync("ping", null, TimeSpan.FromSeconds(20));
        Assert.True(response.TryGetProperty("result", out _));

        lock (observed)
        {
            Assert.Equal(2, observed.Count);
            Assert.Contains("\"method\":\"note\"", observed[0]);
            Assert.Contains("\"result\"", observed[1]);
        }

        Assert.Equal(3, server.Close(10000));
        Assert.Contains("err-line", server.Stderr);
    }
}
