namespace Lurp.Workspace;

/// <summary>
///     Thrown when a solution cannot be opened or its C# projects cannot be read well
///     enough to index.
/// </summary>
/// <remarks>
///     A snapshot indexed from an unbindable compilation reports "no callers" for symbols
///     that have callers, which is more damaging than producing no snapshot at all.
/// </remarks>
internal sealed class WorkspaceUnreadableException : InvalidOperationException
{
    private const string RestoreRemediation =
        "Run 'dotnet restore' on the solution and confirm the required SDK and target frameworks are installed, then re-index.";

    public WorkspaceUnreadableException(string message, Exception? innerException = null, string? remediation = null)
        : base(message, innerException)
    {
        Remediation = remediation ?? RestoreRemediation;
    }

    public string Remediation { get; }
}