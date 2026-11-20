using System.Diagnostics;
using System.Text.Json;

namespace Lurp.Parity.Shared;

/// <summary>
///     Minimal MCP stdio client: one request id at a time, response matched by
///     id. It speaks to a <c>--mode=serve</c> process the caller owns and
///     disposes. Shared by the gate, the perf harness and the tests so the
///     three never drift apart.
/// </summary>
/// <remarks>
///     One background loop reads stdout from start to end of stream and is never
///     cancelled: it hands every line to the observer and completes the pending
///     request whose id the line answers. A request that times out only stops
///     waiting; the stream stays intact, so a line in flight at the timeout is
///     still read whole (a cancelled <see cref="StreamReader.ReadLineAsync(CancellationToken)" />
///     discards the part of a line it has already consumed).
/// </remarks>
public sealed class McpStdioClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly Action<string>? _onStdoutLine;
    private readonly object _sync = new();
    private int _nextId = 1;
    private PendingRequest? _pending;
    private Exception? _undeliveredError;
    private bool _stdoutEnded;
    private Exception? _stdoutFault;

    public McpStdioClient(Process process, Action<string>? onStdoutLine = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _onStdoutLine = onStdoutLine;
        StdoutReader = Task.Run(ReadStdoutAsync);
    }

    /// <summary>
    ///     The stdout reader loop. It completes (never faults) once stdout reaches
    ///     end of stream, after the observer has seen every line.
    /// </summary>
    internal Task StdoutReader { get; }

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
        var pending = new PendingRequest(id, method, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Registered before the request is written, so the reader cannot see the
        // response before there is a request to complete.
        lock (_sync)
        {
            if (_stdoutEnded)
                throw StdoutClosed(pending);
            if (_undeliveredError is { } error)
            {
                _undeliveredError = null;
                throw error;
            }

            _pending = pending;
        }

        try
        {
            await SendLineAsync(request).ConfigureAwait(false);
            return await pending.Response.Task.WaitAsync(timeout ?? DefaultTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"timed out waiting for '{method}' (id={id}) from lurp serve.");
        }
        finally
        {
            lock (_sync)
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
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

    public async Task<JsonElement> CallToolAsync(string name, object? arguments, TimeSpan? timeout = null)
    {
        var response = await SendAsync("tools/call", new { name, arguments = arguments ?? new { } }, timeout).ConfigureAwait(false);
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

    private async Task ReadStdoutAsync()
    {
        Exception? fault = null;
        try
        {
            while (await _stdout.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _onStdoutLine?.Invoke(line);
                if (!string.IsNullOrWhiteSpace(line))
                    Dispatch(line);
            }
        }
        catch (Exception ex)
        {
            // The stream was disposed under the reader, or the observer threw:
            // either way stdout is over for this client.
            fault = ex;
        }

        PendingRequest? pending;
        lock (_sync)
        {
            _stdoutEnded = true;
            _stdoutFault = fault;
            pending = _pending;
            _pending = null;
        }

        pending?.Response.TrySetException(StdoutClosed(pending));
    }

    private void Dispatch(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            FailPendingOrNext(new InvalidOperationException("lurp serve wrote a non-JSON line to stdout: " + line, ex));
            return;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("id", out var idProp)
                || idProp.ValueKind != JsonValueKind.Number
                || !idProp.TryGetInt32(out var id))
                return;

            PendingRequest pending;
            lock (_sync)
            {
                // A response whose request already timed out has nobody waiting.
                if (_pending is not { } current || current.Id != id)
                    return;
                pending = current;
                _pending = null;
            }

            pending.Response.TrySetResult(doc.RootElement.Clone());
        }
    }

    /// <summary>
    ///     A protocol violation fails the request waiting now or, with none waiting,
    ///     the next one, as it did when each request read stdout itself.
    /// </summary>
    private void FailPendingOrNext(Exception error)
    {
        PendingRequest? pending;
        lock (_sync)
        {
            pending = _pending;
            _pending = null;
            if (pending == null)
                _undeliveredError ??= error;
        }

        pending?.Response.TrySetException(error);
    }

    private InvalidOperationException StdoutClosed(PendingRequest pending)
    {
        return new InvalidOperationException(
            $"lurp serve stdout closed while waiting for '{pending.Method}' (id={pending.Id}).", _stdoutFault);
    }

    private sealed record PendingRequest(int Id, string Method, TaskCompletionSource<JsonElement> Response);
}
