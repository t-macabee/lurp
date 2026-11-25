using System.ComponentModel;
using System.Text.Json;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class IndexTool
{
    private readonly McpSessionContext _session;
    private readonly McpIndexSessionState _indexState;
    private readonly Func<string, SqliteIndexStore> _storeFactory;

    public IndexTool(McpSessionContext session, McpIndexSessionState indexState, Func<string, SqliteIndexStore>? storeFactory = null)
    {
        _session = session;
        _indexState = indexState;
        _storeFactory = storeFactory ?? (static path => new SqliteIndexStore(path));
    }

    [McpServerTool(Name = "lurp_index", Title = "Lurp Index", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true,
        OutputSchemaType = typeof(IndexOperationResponse))]
    [Description("Index or re-index the solution the serve session was started with (`--solution=`). Starts a background run (`strategy` full|incremental, `force` to re-extract identical snapshot). Returns at once with {operation_id,status:running}. Progress is buffered; poll lurp_index with {operation_id} to read it. Cancel by calling lurp_index with {operation_id,cancel:true} (sets cancel_requested:true on a running operation). If cancellation stops the run, later polls report cancelled. Reports the snapshot id the run built or reused, whatever database pin is set. On completion do not auto-pin — use lurp_refresh to advance. While running, other tools keep answering from the old pin.")]
    public string LurpIndex(
        string? strategy = null,
        bool? force = null,
        string? operation_id = null,
        bool? cancel = null,
        CancellationToken cancellationToken = default) =>
        McpSessionContext.RunTool(() =>
        {
            // Poll / cancel path when operation_id is supplied.
            if (!string.IsNullOrEmpty(operation_id))
            {
                var snapshot = cancel == true ? _indexState.TryCancel(operation_id) : _indexState.Snapshot(operation_id);
                if (snapshot == null)
                    throw new McpProtocolException($"unknown operation_id '{operation_id}'.", McpErrorCode.InvalidParams);

                return SerializeResponse(snapshot);
            }

            // Start new run — validate single-flight.
            var runningOperationId = _indexState.RunningOperationId;
            if (runningOperationId != null)
                throw new McpProtocolException($"an index is already running (operation_id={runningOperationId}); wait or cancel it before starting another.", McpErrorCode.InvalidParams);

            if (strategy != null)
            {
                var s = strategy.ToLowerInvariant();
                if (!IndexRunner.Strategies.Contains(s, StringComparer.Ordinal))
                    throw new McpProtocolException("strategy must be 'full' or 'incremental'.", McpErrorCode.InvalidParams);
            }

            var solutionPath = _session.SolutionPath;

            if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
                throw new McpProtocolException("no solution configured. Start --mode=serve with --solution=path (or set LURP_SOLUTION_PATH), then index.", McpErrorCode.InvalidParams);

            var dbPath = Path.GetFullPath(_session.DbPath);

            // Acquire locks before starting the operation.
            var solutionLockKey = IndexRunLock.NormalizeKey(solutionPath);
            var solutionLock = IndexRunLock.TryAcquire(solutionLockKey);
            if (solutionLock == null)
                throw new McpProtocolException($"another Lurp index run is using solution {solutionPath}", McpErrorCode.InvalidParams);

            var databaseLockKey = IndexRunLock.NormalizeKey(dbPath);
            var databaseLock = IndexRunLock.TryAcquire(databaseLockKey);
            if (databaseLock == null)
            {
                solutionLock.Dispose();
                throw new McpProtocolException($"another Lurp index run is using database {dbPath}", McpErrorCode.InvalidParams);
            }

            var operationId = Guid.NewGuid().ToString("N");
            IndexOperationSnapshot startSnapshot;
            CancellationTokenSource? cts = null;
            IndexOperation? op = null;
            Task? task = null;
            try
            {
                cts = new CancellationTokenSource();
                op = _indexState.TryStart(operationId, cts, _session.PinnedSnapshotId);
                if (op == null)
                    throw new McpProtocolException("an index is already running; wait or cancel it before starting another.", McpErrorCode.InvalidParams);

                startSnapshot = op.ToSnapshot();

                // The background task uses its own token and owns the locks once started.
                var opCts = cts;
                var opRef = op;
                var opSink = new McpIndexOutputSink(op);
                task = Task.Run(async () =>
                {
                    Action publish;
                    using (solutionLock)
                    using (databaseLock)
                    using (opCts)
                    {
                        try
                        {
                            // Writer store — separate from the session's query_only connection so readers
                            // keep answering from the old pin while the run is active.
                            using var store = _storeFactory(dbPath);
                            store.Open();
                            store.RunMigrations();

                            var skipAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            var selectedSnapshotId = await IndexRunner.RunAsync(store, solutionPath, skipAdapters, strategy, output: opSink, force: force ?? false, cancellationToken: opCts.Token);
                            opSink.WriteLine($"index operation {operationId} completed. snapshot: {selectedSnapshotId}");
                            publish = () => opRef.Complete(selectedSnapshotId);
                        }
                        catch (OperationCanceledException)
                        {
                            opSink.WriteLine($"index operation {operationId} cancelled.");
                            publish = opRef.Cancel;
                        }
                        catch (WorkspaceUnreadableException wue)
                        {
                            opSink.WriteErrorLine($"ERROR: workspace unreadable: {wue.Message}");
                            publish = () => opRef.Fail(wue.Message, "workspace_unreadable", McpErrorMapper.WorkspaceUnreadable(wue));
                        }
                        catch (Exception ex)
                        {
                            opSink.WriteErrorLine($"ERROR: index failed: {ex.Message}");
                            publish = () => opRef.Fail(ex.Message, "index_failed", new { message = ex.Message });
                        }
                    }

                    // Publish the final status only after the store, the locks and the token source
                    // are released: a client that sees it can delete the database or start the next run.
                    publish();
                }, CancellationToken.None);

                op.BackgroundTask = task;
            }
            catch (Exception ex)
            {
                // The task did not start: this method still owns the locks and the CTS.
                // Release them, and mark the operation failed so a later call does not see a
                // stuck "Running".
                if (task == null)
                {
                    solutionLock.Dispose();
                    databaseLock.Dispose();

                    cts?.Dispose();

                    op?.Fail(ex.Message, "index_failed", new { message = ex.Message });
                }

                throw;
            }

            return SerializeResponse(startSnapshot);
        }, cancellationToken);

    private static string SerializeResponse(IndexOperationSnapshot snapshot) =>
        JsonSerializer.Serialize(new IndexOperationResponse(
            snapshot.OperationId, snapshot.Status, snapshot.StartedAtUtc, snapshot.FinishedAtUtc,
            snapshot.Progress, snapshot.ErrorMessage, snapshot.ErrorCode, snapshot.ErrorData,
            snapshot.ResultSnapshotId, snapshot.PreviousSnapshotId, snapshot.CancelRequested), LurpJsonOptions.Indented);

    private sealed class McpIndexOutputSink : IOutputSink
    {
        private readonly IndexOperation _op;

        public McpIndexOutputSink(IndexOperation op)
        {
            _op = op;
        }

        public void Write(string message)
        {
            _op.AppendProgress(message);
        }

        public void WriteLine(string message = "")
        {
            _op.AppendProgress(message);
        }

        public void WriteErrorLine(string message = "")
        {
            _op.AppendProgress("[error] " + message);
        }
    }
}

internal sealed record IndexOperationResponse(
    string operation_id,
    string status,
    DateTime started_at_utc,
    DateTime? finished_at_utc,
    IReadOnlyList<string> progress,
    string? error,
    string? error_code,
    object? error_data,
    string? result_snapshot_id,
    string previous_snapshot_id,
    bool cancel_requested);
