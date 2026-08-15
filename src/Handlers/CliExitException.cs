namespace Lurp.Handlers;

/// <summary>
///     A diagnosed CLI failure: the message is written to stderr and the process
///     terminates with <see cref="ExitCode" />. Thrown by
///     <see cref="HandlerBootstrap.Fail" /> and translated to
///     <see cref="Environment.Exit(int)" /> once, in <c>Program.Main</c>, so no
///     helper hides process control.
/// </summary>
internal sealed class CliExitException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}