namespace Lurp.Storage;

/// <summary>Severity filter values shared by the diagnostics store and the --severity flag.</summary>
public static class DiagnosticSeverityFilters
{
    public static readonly string[] Values = ["Hidden", "Info", "Warning", "Error", "all"];
}
