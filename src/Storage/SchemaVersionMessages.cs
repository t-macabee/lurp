namespace Lurp.Storage;

/// <summary>
///     Single owner of the user-facing schema mismatch texts (without the CLI <c>ERROR: </c> prefix).
/// </summary>
internal static class SchemaVersionMessages
{
    public static string Older(string dbPath, int actualVersion, int expectedVersion) =>
        $"Index database at {dbPath} uses schema v{actualVersion}; this Lurp needs v{expectedVersion}. Run 'lurp --mode=index' to update it.";

    public static string Newer(string dbPath, int actualVersion, int expectedVersion) =>
        $"Index database at {dbPath} uses schema v{actualVersion}, newer than this Lurp (v{expectedVersion}). Update Lurp.";
}
