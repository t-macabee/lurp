namespace Lurp.PerfHarness;

/// <summary>
///     Every skip reason a --solution run records in Program.cs that carries no measurement
///     and therefore does not fail the growth or baseline gate. The literals live here so
///     Program.cs and GateEvaluator compare the same strings.
/// </summary>
internal static class SkipReasons
{
    public const string NeverEdits = "--solution runs never edit files";

    public const string NeverTouches = "--solution runs never touch files";

    public const string NoAnchors = "no anchors available";

    public const string NoAnchorsPassAnchors = "no anchors available (pass --anchors for --solution runs)";
}
