using System.ComponentModel;
using System.Text.Json;
using Lurp.Storage;
using Lurp.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

[McpServerToolType]
internal sealed class IndexTool
{
    private readonly McpSessionContext _session;
    private readonly McpIndexSessionState _indexState;

    public IndexTool(McpSessionContext session, McpIndexSessionState indexState)
    {
        _session = session;
        _indexState = indexState;
    }

    [McpServerTool(Name = "lurp_index", Title = "Lurp Index", ReadOnly = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Index or re-index the solution. Starts a background run (`strategy` full|incremental, `force` to re-extract identical snapshot). Returns at once with {operation_id,status:running}. Progress is buffered; poll lurp_index with {operation_id} to read it. Cancel via MCP cancellation or by calling lurp_index with {operation_id,cancel:true}. On completion do not auto-pin — use lurp_refresh to advance. While running, other tools keep answering from the old pin.")]
    public string LurpIndex(
        string? solution = null,
        string? strategy = null,
        bool? force = null,
        string? operation_id = null,
        bool? cancel = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Poll / cancel path when operation_id is supplied.
            if (!string.IsNullOrEmpty(operation_id))
            {
                if (cancel == true)
                {
                    var ok = _indexState.TryCancel(operation_id!);
                    if (!ok)
                    {
                        var snap = _indexState.Snapshot(operation_id);
                        if (snap == null)
                            throw new McpProtocolException($"unknown operation_id '{operation_id}'.", McpErrorCode.InvalidParams);
                        // Already finished — return its snapshot.
                        return JsonSerializer.Serialize(new
                        {
                            operation_id = snap.OperationId,
                            status = snap.Status,
                            finished_at_utc = snap.FinishedAtUtc,
                            error = snap.ErrorMessage,
                            result_snapshot_id = snap.ResultSnapshotId
                        }, new JsonSerializerOptions { WriteIndented = true });
                    }

                    return JsonSerializer.Serialize(new
                    {
                        operation_id = operation_id,
                        status = "cancelled",
                        message = "cancellation requested"
                    }, new JsonSerializerOptions { WriteIndented = true });
                }

                // Poll status.
                var snapshot = _indexState.Snapshot(operation_id);
                if (snapshot == null)
                    throw new McpProtocolException($"unknown operation_id '{operation_id}'.", McpErrorCode.InvalidParams);

                return JsonSerializer.Serialize(new
                {
                    operation_id = snapshot.OperationId,
                    status = snapshot.Status,
                    started_at_utc = snapshot.StartedAtUtc,
                    finished_at_utc = snapshot.FinishedAtUtc,
                    progress = snapshot.Progress,
                    error = snapshot.ErrorMessage,
                    error_code = snapshot.ErrorCode,
                    error_data = snapshot.ErrorData,
                    result_snapshot_id = snapshot.ResultSnapshotId,
                    previous_snapshot_id = snapshot.PreviousSnapshotId
                }, new JsonSerializerOptions { WriteIndented = true });
            }

            // Start new run — validate single-flight.
            var current = _indexState.Current;
            if (current != null && current.Status == IndexStatus.Running)
                throw new McpProtocolException($"an index is already running (operation_id={current.OperationId}); wait or cancel it before starting another.", McpErrorCode.InvalidParams);

            if (strategy != null)
            {
                var s = strategy.ToLowerInvariant();
                if (s != "full" && s != "incremental")
                    throw new McpProtocolException("strategy must be 'full' or 'incremental'.", McpErrorCode.InvalidParams);
            }

            var solutionPath = string.IsNullOrWhiteSpace(solution)
                ? _session.SolutionPath
                : Path.GetFullPath(solution!);

            if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
                throw new McpProtocolException("solution not found. Provide --solution=path or set LURP_SOLUTION_PATH / --solution on serve.", McpErrorCode.InvalidParams);

            var operationId = Guid.NewGuid().ToString("N");
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var op = _indexState.TryStart(operationId, cts);
            if (op == null)
                throw new McpProtocolException("an index is already running; wait or cancel it before starting another.", McpErrorCode.InvalidParams);

            var previousSnapshotId = _session.PinnedSnapshotId;
            var dbPath = _session.DbPath;
            var strategyArg = strategy;
            var forceFlag = force ?? false;

            // Link external cancellation (MCP notifications/cancelled) to the operation CTS.
            var registration = cancellationToken.Register(() =>
            {
                try { cts.Cancel(); } catch { }
            });

            var sink = new McpIndexOutputSink(op);

            // Background Task — Option B: in-process IndexRunner.RunAsync.
            var task = Task.Run(async () =>
            {
                using var _ = registration;
                // Writer store — separate from the session's query_only connection so readers
                // keep answering from the old pin while the run is active.
                var store = new SqliteIndexStore(dbPath);
                try
                {
                    store.Open();
                    store.RunMigrations();
                    store.ValidateSchema(VersionConstants.DatabaseSchemaVersion);

                    var skipAdapters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    await IndexRunner.RunAsync(store, solutionPath!, skipAdapters, null, strategyArg, verbose: false, output: sink, skipDiff: false, force: forceFlag, cancellationToken: cts.Token);

                    // On success, detect the new complete snapshot (may be identical to previous when dedup reused).
                    string? latest = null;
                    try { latest = store.GetLatestSnapshotId(); } catch { }

                    // If no new snapshot (dedup reuse), latest == previous. Still mark completed.
                    op.Complete(latest, previousSnapshotId);

                    sink.WriteLine($"index operation {operationId} completed. snapshot: {latest ?? previousSnapshotId}");
                }
                catch (OperationCanceledException)
                {
                    // IndexRunner already did MarkSnapshotFailed(..., "cancelled", ...). Ensure state reflects it.
                    // Confirm no partial Complete row is visible: GetLatestSnapshotId still returns the old pin.
                    // The Failed row is a tombstone with payload_pruned; it must not be returned as latest.
                    try
                    {
                        // Best-effort cleanup of payload for the cancelled attempt.
                        // DeleteIncompleteSnapshots would normally prune on the next successful run;
                        // we trigger it now so a test that checks "no partial data" sees the old pin unchanged.
                        // This respects snapshot-immutability: a Failed row is not a Complete snapshot,
                        // and its payload is unreferenced once cancelled — pruning it does not mutate a
                        // Complete snapshot.
                        var tmp = new SqliteIndexStore(dbPath);
                        tmp.Open();
                        try { tmp.DeleteIncompleteSnapshots(); } finally { tmp.Close(); }
                    }
                    catch { }

                    op.Cancel();
                    sink.WriteLine($"index operation {operationId} cancelled.");
                }
                catch (WorkspaceUnreadableException wue)
                {
                    // Return structured data, not a stack trace (4.9).
                    var data = new
                    {
                        reason_code = "workspace_unreadable",
                        message = wue.Message,
                        solution = solutionPath
                    };
                    op.Fail(wue.Message, "workspace_unreadable", data);
                    sink.WriteErrorLine($"ERROR: workspace unreadable: {wue.Message}");
                }
                catch (Exception ex) when (ex is not McpProtocolException)
                {
                    op.Fail(ex.Message, "index_failed", new { message = ex.Message });
                    sink.WriteErrorLine($"ERROR: index failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    op.Fail(ex.Message);
                    sink.WriteErrorLine($"ERROR: index failed: {ex.Message}");
                }
                finally
                {
                    try { store.Close(); } catch { }
                }
            }, CancellationToken.None);

            op.BackgroundTask = task;

            var envelope = new
            {
                operation_id = operationId,
                status = "running",
                started_at_utc = op.StartedAtUtc,
                previous_snapshot_id = previousSnapshotId
            };
            return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }

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
