using System.Diagnostics;
using System.Text.Json;

namespace Lurp.Parity.Shared;

/// <summary>
///     Minimal MCP stdio client: one request id at a time, response matched by
///     id. It speaks to a <c>--mode=serve</c> process the caller owns and
///     disposes. Shared by <c>TargetTreeIntegrityTests</c> and the parity gate
///     so the two never drift apart.
/// </summary>
public sealed class McpStdioClient
{
    private readonly Func<string, Task> _send;
    private readonly Func<CancellationToken, Task<string?>> _readLine;
    private int _nextId = 1;

    public McpStdioClient(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var stdin = process.StandardInput;
        var stdout = process.StandardOutput;
        _send = async line =>
        {
            await stdin.WriteLineAsync(line).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
        };
        _readLine = cancellationToken => stdout.ReadLineAsync(cancellationToken).AsTask();
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters)
    {
        var id = _nextId++;
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
        await _send(request).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            while (true)
            {
                var line = await _readLine(cts.Token).ConfigureAwait(false);
                if (line == null)
                    throw new InvalidOperationException($"lurp serve stdout closed while waiting for '{method}' (id={id}).");
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("id", out var idProp)
                    && idProp.ValueKind == JsonValueKind.Number
                    && idProp.GetInt32() == id)
                    return doc.RootElement.Clone();
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"timed out waiting for '{method}' (id={id}) from lurp serve.");
        }
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
        await _send(request).ConfigureAwait(false);
    }
}
