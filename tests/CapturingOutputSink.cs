using Lurp.Workspace;
using System.Text;

namespace Lurp.Tests;

internal sealed class CapturingOutputSink : IOutputSink
{
    public StringBuilder Output { get; } = new();

    public void Write(string message)
    {
        Output.Append(message);
    }

    public void WriteLine(string message = "")
    {
        Output.AppendLine(message);
    }

    public void WriteErrorLine(string message = "")
    {
        Output.AppendLine(message);
    }
}
