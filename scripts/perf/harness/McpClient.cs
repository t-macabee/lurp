using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal sealed class McpClient : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderrTask;
    private int _nextId;
    private bool _disposed;

    public McpClient(string[] argv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = argv[0],
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        for (var i = 1; i < argv.Length; i++)
            psi.ArgumentList.Add(argv[i]);

        _process = Process.Start(psi) ?? throw new HarnessError($"could not start '{argv[0]}' for --mode=serve.");
        _stderrTask = _process.StandardError.ReadToEndAsync();
    }

    public string Stderr => _stderrTask.IsCompleted ? _stderrTask.GetAwaiter().GetResult() : "";

    public void Initialize(int timeoutMs)
    {
        var id = ++_nextId;
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "lurp-perf-harness",
                    ["version"] = "1.0"
                }
            }
        });

        var response = ReadResponse(id, timeoutMs)
                       ?? throw new HarnessError($"MCP initialize got no response within {timeoutMs} ms.");
        if (response["error"] is JsonNode error)
            throw new HarnessError($"MCP initialize failed: {error.ToJsonString()}");

        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized"
        });
    }

    public (long LatencyMs, long PeakWorkingSetMb, string? Error) Call(string tool, JsonObject arguments, int timeoutMs)
    {
        var id = ++_nextId;
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = tool,
                ["arguments"] = arguments.DeepClone()
            }
        };

        var sampler = new PeakSampler(_process);
        sampler.Start();
        var stopwatch = Stopwatch.StartNew();
        JsonObject? response;
        try
        {
            Send(request);
            response = ReadResponse(id, timeoutMs);
        }
        finally
        {
            stopwatch.Stop();
            sampler.Stop();
        }

        if (response is null)
            return (stopwatch.ElapsedMilliseconds, sampler.PeakMb, $"tools/call {tool} got no response within {timeoutMs} ms.");
        if (response["error"] is JsonNode error)
            return (stopwatch.ElapsedMilliseconds, sampler.PeakMb, $"tools/call {tool} failed: {Truncate(error.ToJsonString(), 500)}");
        if (response["result"] is JsonObject result && result["isError"]?.GetValue<bool>() == true)
            return (stopwatch.ElapsedMilliseconds, sampler.PeakMb, $"tools/call {tool} returned an error result: {Truncate(result["content"]?.ToJsonString(), 500)}");

        return (stopwatch.ElapsedMilliseconds, sampler.PeakMb, null);
    }

    public int Close()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        if (!_process.WaitForExit(60000))
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }

        return _process.ExitCode;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        _process.Dispose();
    }

    private void Send(JsonObject message)
    {
        _process.StandardInput.WriteLine(message.ToJsonString());
        _process.StandardInput.Flush();
    }

    private JsonObject? ReadResponse(int id, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                return null;

            var lineTask = _process.StandardOutput.ReadLineAsync();
            if (!lineTask.Wait((int)Math.Min(remaining, int.MaxValue)))
                return null;

            var line = lineTask.GetAwaiter().GetResult();
            if (line is null)
                return null;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }

            if (message?["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var responseId) && responseId == id)
                return message;
        }
    }

    private static string Truncate(string? text, int maxLength)
        => text is null ? "" : text.Length <= maxLength ? text : text[..maxLength];
}

internal sealed class PeakSampler
{
    private readonly Process _process;
    private readonly object _sync = new();
    private Task? _task;
    private volatile bool _stop;
    private long _peakBytes;

    public PeakSampler(Process process) => _process = process;

    public void Start()
    {
        _task = Task.Run(() =>
        {
            while (!_stop)
            {
                var value = ProcessRunner.SamplePeak(_process);
                lock (_sync)
                {
                    if (value > _peakBytes)
                        _peakBytes = value;
                }

                Thread.Sleep(ProcessRunner.PollIntervalMs);
            }
        });
    }

    public void Stop()
    {
        _stop = true;
        try
        {
            _task?.Wait(5000);
        }
        catch (AggregateException)
        {
        }
    }

    public long PeakMb
    {
        get
        {
            lock (_sync)
                return ProcessRunner.ToMegabytes(_peakBytes);
        }
    }
}
