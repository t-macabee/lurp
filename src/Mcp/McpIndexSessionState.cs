using System.Collections.Concurrent;

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

    /// <summary>
    ///     Try to start a new operation. Returns the operation on success, or null when
    ///     another run is already active — the caller must then reject with -32602.
    /// </summary>
    public IndexOperation? TryStart(string operationId, CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_current != null && _current.Status == IndexStatus.Running)
                return null;

            var op = new IndexOperation(operationId, cts);
            _current = op;
            return op;
        }
    }

    public bool TryCancel(string operationId)
    {
        lock (_lock)
        {
            if (_current == null) return false;
            if (_current.OperationId != operationId) return false;
            if (_current.Status != IndexStatus.Running) return false;
            try { _current.CancellationTokenSource.Cancel(); } catch { }
            return true;
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
    private readonly object _progressLock = new();

    public string OperationId { get; }
    public CancellationTokenSource CancellationTokenSource { get; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; private set; }
    public IndexStatus Status { get; private set; } = IndexStatus.Running;
    public string? ErrorMessage { get; private set; }
    public string? ErrorCode { get; private set; }
    public object? ErrorData { get; private set; }
    public string? ResultSnapshotId { get; private set; }
    public string? PreviousSnapshotId { get; private set; }
    public List<string>? UnrestoredProjects { get; private set; }
    public Task? BackgroundTask { get; set; }

    public IndexOperation(string operationId, CancellationTokenSource cts)
    {
        OperationId = operationId;
        CancellationTokenSource = cts;
    }

    public void AppendProgress(string line)
    {
        lock (_progressLock) _progressLines.Add(line);
    }

    public IReadOnlyList<string> GetProgress()
    {
        lock (_progressLock) return _progressLines.ToList();
    }

    public void SetUnrestoredProjects(List<string>? projects)
    {
        UnrestoredProjects = projects;
    }

    public void Complete(string? newSnapshotId, string? previousSnapshotId)
    {
        FinishedAtUtc = DateTime.UtcNow;
        Status = IndexStatus.Completed;
        ResultSnapshotId = newSnapshotId;
        PreviousSnapshotId = previousSnapshotId;
    }

    public void Fail(string message, string? code = null, object? data = null)
    {
        FinishedAtUtc = DateTime.UtcNow;
        Status = IndexStatus.Failed;
        ErrorMessage = message;
        ErrorCode = code;
        ErrorData = data;
    }

    public void Cancel()
    {
        FinishedAtUtc = DateTime.UtcNow;
        Status = IndexStatus.Cancelled;
    }

    public IndexOperationSnapshot ToSnapshot()
    {
        return new IndexOperationSnapshot(
            OperationId,
            Status.ToString().ToLowerInvariant(),
            StartedAtUtc,
            FinishedAtUtc,
            GetProgress(),
            ErrorMessage,
            ErrorCode,
            ErrorData,
            ResultSnapshotId,
            PreviousSnapshotId,
            UnrestoredProjects);
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
    string? PreviousSnapshotId,
    List<string>? UnrestoredProjects);
