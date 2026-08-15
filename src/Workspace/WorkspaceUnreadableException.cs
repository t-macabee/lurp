namespace Lurp.Workspace;

/// <summary>
///     Thrown when no project in the solution produced a bindable compilation, so the
///     snapshot would describe an empty graph as though it were a proved one.
/// </summary>
/// <remarks>
///     This is a build-environment fault rather than a defect in the code under analysis:
///     it means MSBuild never resolved a reference set. Failing loudly here is deliberate.
///     A snapshot indexed from an unbindable compilation reports "no callers" for symbols
///     that have callers, which is more damaging than producing no snapshot at all.
/// </remarks>
internal sealed class WorkspaceUnreadableException(string message) : InvalidOperationException(message);