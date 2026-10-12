using System.Diagnostics;
using System.Text.Json;

namespace Lurp.Parity.Shared;

/// <summary>
///     Minimal MCP stdio client: one request id at a time, response matched by
///     id. It speaks to a <c>--mode=serve</c> process the caller owns and
///     disposes. Shared by the gate, the perf harness and the tests so the
///     three never drift apart.
/// </summary>
public sealed class McpStdioClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly Action<string>? _onStdoutLine;
    private int _nextId = 1;

    public McpStdioClient(Process process, Action<string>? onStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _onStdoutLine = onStdoutLine;
    }

    /// <summary>
    ///     Writes one raw line to the server's stdin and flushes it. The single
    ///     write path shared by every request and notification.
    /// </summary>
    public async Task SendLineAsync(string line)
    {
        await _stdin.WriteLineAsync(line).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters, TimeSpan? timeout = null)
    {
        var id = _nextId++;
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await SendLineAsync(request).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        try
        {
            while (true)
            {
                var line = await _stdout.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line == null)
                    throw new InvalidOperationException($"lurp serve stdout closed while waiting for '{method}' (id={id}).");
                _onStdoutLine?.Invoke(line);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("lurp serve wrote a non-JSON line to stdout: " + line, ex);
                }

                using (doc)
                {
                    if (doc.RootElement.TryGetProperty("id", out var idProp)
                        && idProp.ValueKind == JsonValueKind.Number
                        && idProp.GetInt32() == id)
                        return doc.RootElement.Clone();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"timed out waiting for '{method}' (id={id}) from lurp serve.");
        }
    }

    /// <summary>
    ///     Performs the MCP handshake: <c>initialize</c> with protocol
    ///     <c>2024-11-05</c> and the given client name, then the
    ///     <c>notifications/initialized</c> notification. Returns the response.
    /// </summary>
    public async Task<JsonElement> InitializeAsync(string clientName, TimeSpan? timeout = null)
    {
        var response = await SendAsync(
            "initialize",
            new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = clientName, version = "1.0" }
            },
            timeout).ConfigureAwait(false);

        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException("MCP initialize failed: " + error);

        await NotifyAsync("notifications/initialized", new { }).ConfigureAwait(false);
        return response;
    }

    public async Task<JsonElement> CallToolAsync(string name, object? arguments)
    {
        var response = await SendAsync("tools/call", new { name, arguments = arguments ?? new { } }).ConfigureAwait(false);
        if (response.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"MCP tool {name} returned an error: {error}");

        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException($"MCP tool {name} returned isError: {result}");

        return result.Clone();
    }

    public async Task NotifyAsync(string method, object? parameters)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        await SendLineAsync(request).ConfigureAwait(false);
    }
}
