using Lurp.Handlers;
using Lurp.Workspace;
using System.Text;

namespace Lurp.Tests;

/// <summary>
///     Test stand-in for the CLI output sink. <see cref="Stdout" /> and <see cref="Stderr" /> hold each stream on its own;
///     <see cref="Output" /> holds both interleaved in write order, and <see cref="ErrorLines" /> each stderr line.
///     <see cref="Capture" /> and <see cref="CaptureAsync" /> run a handler against a fresh sink and record a
///     <see cref="CliExitException" /> as <see cref="Failure" /> instead of propagating it.
/// </summary>
internal sealed class CapturingOutputSink : IOutputSink
{
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();

    public StringBuilder Output { get; } = new();

    public List<string> ErrorLines { get; } = [];

    public string Stdout => _stdout.ToString();

    public string Stderr => _stderr.ToString();

    public CliExitException? Failure { get; private set; }

    public void Write(string message)
    {
        _stdout.Append(message);
        Output.Append(message);
    }

    public void WriteLine(string message = "")
    {
        _stdout.AppendLine(message);
        Output.AppendLine(message);
    }

    public void WriteErrorLine(string message = "")
    {
        _stderr.AppendLine(message);
        Output.AppendLine(message);
        ErrorLines.Add(message);
    }

    internal static CapturingOutputSink Capture(Action<IOutputSink> action)
    {
        var sink = new CapturingOutputSink();
        try
        {
            action(sink);
        }
        catch (CliExitException ex)
        {
            sink.Failure = ex;
        }

        return sink;
    }

    internal static async Task<CapturingOutputSink> CaptureAsync(Func<IOutputSink, Task> action)
    {
        var sink = new CapturingOutputSink();
        try
        {
            await action(sink);
        }
        catch (CliExitException ex)
        {
            sink.Failure = ex;
        }

        return sink;
    }
}
