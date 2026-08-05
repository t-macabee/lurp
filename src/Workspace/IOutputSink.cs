namespace Lurp.Workspace;

/// <summary>
/// Output sink for indexing-pipeline progress/warning/error messages. Both
/// <see cref="IndexRunner"/> and <see cref="IncrementalIndexer"/> route every
/// message through this interface instead of calling Console directly, so a
/// caller can capture or redirect pipeline output (tests, an MCP host, a
/// structured logger) without the pipeline knowing about it.
/// </summary>
public interface IOutputSink
{
    /// <summary>Writes text with no trailing newline (mirrors <see cref="Console.Write(string?)"/>).</summary>
    void Write(string message);

    /// <summary>Writes a line to standard output (mirrors <see cref="Console.WriteLine(string?)"/>).</summary>
    void WriteLine(string message = "");

    /// <summary>Writes a line to standard error (mirrors <see cref="Console.Error"/>'s <c>WriteLine</c>).</summary>
    void WriteErrorLine(string message = "");
}

/// <summary>
/// Default <see cref="IOutputSink"/> : reproduces the exact Console behavior
/// both pipelines used before the sink existed.
/// </summary>
public sealed class ConsoleOutputSink : IOutputSink
{
    public static readonly ConsoleOutputSink Instance = new();

    public void Write(string message) => Console.Write(message);
    public void WriteLine(string message = "") => Console.WriteLine(message);
    public void WriteErrorLine(string message = "") => Console.Error.WriteLine(message);
}
