namespace Lurp.Storage;

/// <summary>
///     Thrown by <see cref="SnapshotDocumentStore.GetSourceSlice" /> when the requested
///     1-based start line exceeds the document's line count (D189.2).
/// </summary>
public sealed class SourceLineOutOfRangeException(int requestedStartLine, int totalLines)
    : Exception($"start_line {requestedStartLine} is beyond the last line ({totalLines}).")
{
    public int RequestedStartLine { get; } = requestedStartLine;
    public int TotalLines { get; } = totalLines;
}
