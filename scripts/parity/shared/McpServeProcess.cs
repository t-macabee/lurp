using System.Diagnostics;
using System.Text;

namespace Lurp.Parity.Shared;

/// <summary>
///     Owns one <c>lurp --mode=serve</c> child process: starts it with stdio
///     redirected, exposes the shared <see cref="McpStdioClient" />, drains
///     stderr, and shuts the process down deterministically.
/// </summary>
public sealed class McpServeProcess : IDisposable
{
    private readonly Task _stderrTask;
    private readonly StringBuilder _stderr;
    private readonly object _stderrSync;
    private bool _disposed;
    private int _closed;

    private McpServeProcess(Process process, Task stderrTask, StringBuilder stderr, object stderrSync, McpStdioClient client)
    {
        Process = process;
        _stderrTask = stderrTask;
        _stderr = stderr;
        _stderrSync = stderrSync;
        Client = client;
    }

    public Process Process { get; }

    public McpStdioClient Client { get; }

    /// <summary>The stderr text read so far. Thread-safe.</summary>
    public string Stderr
    {
        get
        {
            lock (_stderrSync)
                return _stderr.ToString();
        }
    }

    public static McpServeProcess Start(IReadOnlyList<string> argv, Action<string>? onStdoutLine = null)
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
        for (var i = 1; i < argv.Count; i++)
            psi.ArgumentList.Add(argv[i]);

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"could not start '{argv[0]}' for --mode=serve.");
        var stderr = new StringBuilder();
        var stderrSync = new object();
        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (read <= 0)
                    break;
                lock (stderrSync)
                    stderr.Append(buffer, 0, read);
            }
        });

        return new McpServeProcess(process, stderrTask, stderr, stderrSync, new McpStdioClient(process, onStdoutLine));
    }

    public int Close(int timeoutMs)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return Process.ExitCode;

        try
        {
            Process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (!Process.WaitForExit(timeoutMs))
        {
            try
            {
                Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            Process.WaitForExit();
        }

        try
        {
            _stderrTask.Wait(5000);
        }
        catch (AggregateException)
        {
        }

        return Process.ExitCode;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (!Process.HasExited)
                Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        Process.Dispose();
    }
}
