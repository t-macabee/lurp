using System.ComponentModel;
using System.Text.Json;
using Lurp.Handlers;
using Lurp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Lurp.Mcp.Tools;

/// <summary>
///     Split from <see cref="AnnotationsTool" /> so the write half of the
///     annotation surface is only registered when <c>--enable-write-tools</c>
///     is passed to <c>--mode=serve</c>.
/// </summary>
[McpServerToolType]
internal sealed class RetractAnnotationTool
{
    private readonly McpSessionContext _session;

    public RetractAnnotationTool(McpSessionContext session)
    {
        _session = session;
    }

    [McpServerTool(Name = "lurp_retract_annotation", Title = "Lurp Retract Annotation", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Retract (hard-delete) one annotation by annotation_id, scoped to the pinned snapshot — any provenance, not limited to user-authored (lurp_annotate) rows; extractor-derived rows can be removed the same way. The annotation_id is the surrogate PK from lurp_get_annotations. The delete is WHERE snapshot_id=@pinned AND annotation_id=@id — one row only, no cross-snapshot effect. Copy-forward clones allocate fresh ids, so retracting in snapshot A does not affect snapshot B's clone.")]
    public string LurpRetractAnnotation(
        long annotation_id,
        string? snapshot_id = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (annotation_id <= 0)
                throw new McpProtocolException("annotation_id must be a positive integer.", McpErrorCode.InvalidParams);

            var snapshotId = _session.RequirePinnedSnapshot(snapshot_id);

            // The session's read connections are read-only; retraction requires a
            // short-lived writable connection against the same DbPath and pin scope.
            HandlerBootstrap.RequireCurrentSchemaVersion(_session.DbPath);
            var writable = new SqliteIndexStore(_session.DbPath);
            writable.Open();
            try
            {
                bool deleted;
                try
                {
                    deleted = writable.TryRetractAnnotation(snapshotId, annotation_id);
                }
                catch (ArgumentException ex)
                {
                    throw new McpProtocolException(ex.Message, McpErrorCode.InvalidParams);
                }

                if (!deleted)
                    throw new McpProtocolException($"annotation_id {annotation_id} not found in snapshot '{snapshotId}'.", McpErrorCode.InvalidParams);

                var freshness = _session.GetFreshnessJson();
                var envelope = new
                {
                    status = "ok",
                    snapshot_id = snapshotId,
                    annotation_id,
                    retracted = true,
                    freshness,
                    pinned = true
                };
                return JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            }
            finally
            {
                writable.Close();
            }
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw McpErrorMapper.Map(ex);
        }
    }
}
