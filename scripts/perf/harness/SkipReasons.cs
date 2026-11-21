namespace Lurp.PerfHarness;

/// <summary>
///     Skip reasons the harness records for solution runs. Only the baseline gate
///     accepts NeverEdits and NeverTouches.
/// </summary>
internal static class SkipReasons
{
    public const string NeverEdits = "--solution runs never edit files";

    public const string NeverTouches = "--solution runs never touch files";

    public const string NoAnchors = "no anchors available";

    public const string NoAnchorsPassAnchors = "no anchors available (pass --anchors for --solution runs)";
}
