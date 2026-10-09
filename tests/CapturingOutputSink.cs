using Lurp.Workspace;
using System.Text;

namespace Lurp.Tests;

/// <summary>Records everything written to the sink in <see cref="Output" /> and each error line in <see cref="ErrorLines" />.</summary>
internal sealed class CapturingOutputSink : IOutputSink
{
    public StringBuilder Output { get; } = new();

    public List<string> ErrorLines { get; } = [];

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
        ErrorLines.Add(message);
    }
}
