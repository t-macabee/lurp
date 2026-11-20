namespace Lurp.Mcp;

/// <summary>Tracks one in-flight <c>lurp_index</c> run per MCP session.</summary>
internal sealed class McpIndexSessionState
{
    private readonly object _lock = new();
    private IndexOperation? _current;

    public IndexOperation? Current
    {
        get { lock (_lock) return _current; }
    }

    public string? RunningOperationId
    {
        get { lock (_lock) return _current?.Status == IndexStatus.Running ? _current.OperationId : null; }
    }

    /// <summary>
    ///     Try to start a new operation. Returns the operation on success, or null when
    ///     another run is already active — the caller must then reject the request.
    /// </summary>
    public IndexOperation? TryStart(string operationId, CancellationTokenSource cts, string previousSnapshotId)
    {
        lock (_lock)
        {
            if (_current != null && _current.Status == IndexStatus.Running)
                return null;

            var op = new IndexOperation(operationId, cts, previousSnapshotId, _lock);
            _current = op;
            return op;
        }
    }

    public IndexOperationSnapshot? TryCancel(string operationId)
    {
        lock (_lock)
        {
            if (_current?.OperationId != operationId)
                return null;

            if (_current.Status == IndexStatus.Running)
                _current.RequestCancellation();

            return _current.ToSnapshot();
        }
    }

    /// <summary>
    ///     Cancels the currently tracked run, if any, without requiring its id.
    ///     Used by host shutdown to stop an in-flight run before the process exits.
    /// </summary>
    public void CancelCurrent()
    {
        lock (_lock)
        {
            if (_current?.Status == IndexStatus.Running)
                _current.RequestCancellation();
        }
    }

    /// <summary>Snapshot for JSON serialization.</summary>
    public IndexOperationSnapshot? Snapshot(string? operationId = null)
    {
        lock (_lock)
        {
            if (_current == null) return null;
            if (operationId != null && _current.OperationId != operationId) return null;
            return _current.ToSnapshot();
        }
    }
}

internal enum IndexStatus
{
    Running,
    Completed,
    Failed,
    Cancelled
}

internal sealed class IndexOperation
{
    private readonly List<string> _progressLines = new();
    private readonly object _lock;

    public string OperationId { get; }
    public CancellationTokenSource CancellationTokenSource { get; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; private set; }
    public IndexStatus Status { get; private set; } = IndexStatus.Running;
    public string? ErrorMessage { get; private set; }
    public string? ErrorCode { get; private set; }
    public object? ErrorData { get; private set; }
    public string? ResultSnapshotId { get; private set; }
    public string PreviousSnapshotId { get; }
    public bool CancelRequested { get; private set; }
    public Task? BackgroundTask { get; set; }

    public IndexOperation(string operationId, CancellationTokenSource cts, string previousSnapshotId, object stateLock)
    {
        OperationId = operationId;
        CancellationTokenSource = cts;
        PreviousSnapshotId = previousSnapshotId;
        _lock = stateLock;
    }

    public void AppendProgress(string line)
    {
        lock (_lock) _progressLines.Add(line);
    }

    // Called under the session lock so cancellation and the returned status agree.
    internal void RequestCancellation()
    {
        CancelRequested = true;
        try { CancellationTokenSource.Cancel(); } catch { }
    }

    public void Complete(string newSnapshotId)
    {
        lock (_lock)
        {
            FinishedAtUtc = DateTime.UtcNow;
            Status = IndexStatus.Completed;
            ResultSnapshotId = newSnapshotId;
        }
    }

    public void Fail(string message, string? code = null, object? data = null)
    {
        lock (_lock)
        {
            FinishedAtUtc = DateTime.UtcNow;
            Status = IndexStatus.Failed;
            ErrorMessage = message;
            ErrorCode = code;
            ErrorData = data;
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            FinishedAtUtc = DateTime.UtcNow;
            Status = IndexStatus.Cancelled;
        }
    }

    public IndexOperationSnapshot ToSnapshot()
    {
        lock (_lock)
        {
            return new IndexOperationSnapshot(
                OperationId,
                Status.ToString().ToLowerInvariant(),
                StartedAtUtc,
                FinishedAtUtc,
                _progressLines.ToArray(),
                ErrorMessage,
                ErrorCode,
                ErrorData,
                ResultSnapshotId,
                PreviousSnapshotId,
                CancelRequested);
        }
    }
}

internal sealed record IndexOperationSnapshot(
    string OperationId,
    string Status,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    IReadOnlyList<string> Progress,
    string? ErrorMessage,
    string? ErrorCode,
    object? ErrorData,
    string? ResultSnapshotId,
    string PreviousSnapshotId,
    bool CancelRequested);
