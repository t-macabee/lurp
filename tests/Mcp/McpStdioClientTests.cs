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

    [Fact]
    public async Task Close_ObserverSeesStdoutWrittenAfterLastResponse()
    {
        // The stand-in writes its last line only once Close has closed its stdin, and
        // the observer is slow on that line, so Close returns before the observer has
        // it unless Close waits for the stdout reader to reach end of stream.
        var script =
            "$null = [Console]::In.ReadLine(); " +
            "Write-Output '{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'; " +
            "$null = [Console]::In.ReadToEnd(); " +
            "Write-Output 'shutdown-log'; exit 0";
        var observed = new List<string>();
        using var server = McpServeProcess.Start(
            Pwsh(script),
            line =>
            {
                if (line == "shutdown-log")
                    Thread.Sleep(500);
                lock (observed)
                    observed.Add(line);
            });

        await server.Client.SendAsync("ping", null, TimeSpan.FromSeconds(20));
        lock (observed)
            Assert.Single(observed);

        Assert.Equal(0, server.Close(10000));

        lock (observed)
        {
            Assert.Equal(2, observed.Count);
            Assert.Equal("shutdown-log", observed[1]);
        }
    }

    [Fact]
    public async Task TimeoutMidLine_KeepsTheLineWhole_AndTheNextRequestSucceeds()
    {
        // Request 2 times out after the stand-in has written only the first half of
        // its response line. The rest arrives later; the line must still be read
        // whole, and request 3 must get its own response rather than the tail of 2.
        var script =
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}'); [Console]::Out.Flush(); " +
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.Write('{\"jsonrpc\":\"2.0\",\"id\":2,'); [Console]::Out.Flush(); " +
            "Start-Sleep -Milliseconds 3000; " +
            "[Console]::Out.WriteLine('\"result\":{}}'); [Console]::Out.Flush(); " +
            "$null = [Console]::In.ReadLine(); " +
            "[Console]::Out.WriteLine('{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{}}'); [Console]::Out.Flush(); " +
            "$null = [Console]::In.ReadToEnd(); exit 0";
        var observed = new List<string>();
        using var server = McpServeProcess.Start(
            Pwsh(script),
            line =>
            {
                lock (observed)
                    observed.Add(line);
            });

        // Request 1 waits out process start-up, so request 2's short timeout runs
        // while its response line is half written.
        await server.Client.SendAsync("ping", null, TimeSpan.FromSeconds(20));
        await Assert.ThrowsAsync<TimeoutException>(
            () => server.Client.SendAsync("slow", null, TimeSpan.FromMilliseconds(1000)));

        var third = await server.Client.SendAsync("ping", null, TimeSpan.FromSeconds(20));
        Assert.Equal(3, third.GetProperty("id").GetInt32());

        Assert.Equal(0, server.Close(10000));
        lock (observed)
            Assert.Equal(
                [
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{}}",
                    "{\"jsonrpc\":\"2.0\",\"id\":3,\"result\":{}}"
                ],
                observed);
    }
}
