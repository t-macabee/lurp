namespace Lurp.Workspace;

internal static class VersionConstants
{
    internal const int DatabaseSchemaVersion = 37;
    internal const int OutputSchemaVersion = 6;
    // Bump the version of each edge extractor whose output changes (ExtractorConstants, or an
    // adapter's version). Bump this value for every other change to extraction output:
    // declarations, symbol metadata, structural edges, binding-incompleteness rows. Both
    // versions are part of the snapshot identity, so either bump produces a new snapshot.
    internal const string ExtractorVersion = "1.11.0";
    internal const string ToolVersion = "3.6.0";
    internal const int CliMcpContractVersion = 6;
}