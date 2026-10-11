namespace Lurp.Workspace;

internal static class VersionConstants
{
    internal const int DatabaseSchemaVersion = 36;
    internal const int OutputSchemaVersion = 5;
    // Bump this value with every change to any extractor's output; the per-extractor
    // versions alone do not trigger a new snapshot.
    internal const string ExtractorVersion = "1.9.0";
    internal const string ToolVersion = "2.0.0";
    internal const int CliMcpContractVersion = 5;
}