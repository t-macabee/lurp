using System.Text.Json.Nodes;

namespace Lurp.PerfHarness;

internal sealed class HarnessError : Exception
{
    public HarnessError()
    {
    }

    public HarnessError(string message) : base(message)
    {
    }

    public HarnessError(string message, Exception inner) : base(message, inner)
    {
    }
}

internal sealed record AnchorSet(string UpstreamWide, string DownstreamWide, string Middle, string? EditDocument);

internal sealed record RunRecord(
    long WallMs,
    long PeakWorkingSetMb,
    int ExitCode,
    long? DbBytes = null,
    long? MetricPeakWorkingSetMb = null,
    string? PeakCrossCheck = null,
    string? Error = null,
    long? DbDeltaBytes = null,
    long? TimingsTotalMs = null,
    JsonArray? TimingsSteps = null);

internal sealed class OperationRecord
{
    public required string Name { get; init; }

    public string? Anchor { get; init; }

    public bool Skipped { get; set; }

    public string? SkipReason { get; set; }

    public bool Failed { get; set; }

    public string? Failure { get; set; }

    public int? ServerExitCode { get; set; }

    public int? TouchedFiles { get; set; }

    public string? FreshnessState { get; set; }

    public int? FreshnessChangedDocumentCount { get; set; }

    public List<RunRecord> Runs { get; } = [];
}
