using System.Reflection;

namespace Lurp.Workspace;

internal static class VersionConstants
{
    internal const int OutputSchemaVersion = 8;
    // Move this value when any extraction output changes, and move the string of each extractor
    // whose rows change (ExtractorConstants, an adapter's Version). ExtractorVersionGateTests and
    // tests/Snapshots/extractor-versions.txt enforce the rule (VERSIONING.md, D103).
    internal const string ExtractorVersion = "1.14.0";

    // The csproj <Version> is the one literal of the tool version (D105). The SDK copies it into
    // AssemblyInformationalVersionAttribute and appends "+<commit>" when it builds a git checkout.
    internal static string ToolVersion { get; } =
        (typeof(VersionConstants).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
         ?? throw new InvalidOperationException("The Lurp assembly has no AssemblyInformationalVersionAttribute."))
        .InformationalVersion.Split('+')[0];

    internal const int CliMcpContractVersion = 9;
}
