namespace Lurp.Shared;

/// <summary>
///     Shared error messages for document-scoped readers across CLI and MCP (D189.4).
/// </summary>
internal static class DocumentMessages
{
    public static string NotFound(string document, string snapshotId) =>
        $"Document '{document}' not found in snapshot '{snapshotId}'.";
}
