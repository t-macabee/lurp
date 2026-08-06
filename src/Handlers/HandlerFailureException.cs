namespace Lurp.Handlers;

/// <summary>
/// A diagnosed handler refusal carrying the process exit code the CLI should use.
/// <para>
/// <see cref="HandlerBootstrap.Fail"/> used to call <see cref="Environment.Exit(int)"/>
/// directly, which is correct for a one-shot CLI and fatal for any long-lived host:
/// a single malformed request would terminate the whole process. Throwing instead
/// keeps the failure recoverable by the caller, and <c>Program</c> restores the exact
/// CLI behaviour (message to stderr, that exit code) at the top level.
/// </para>
/// </summary>
internal sealed class HandlerFailureException : Exception
{
    public HandlerFailureException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}
