namespace Lurp.Storage;

public static class SnapshotMetricNames
{
    /// <summary>
    ///     Exact peak working set of the Lurp process, in bytes. This value has full precision.
    /// </summary>
    public const string PeakWorkingSetBytes = "peak_working_set_bytes";

    /// <summary>
    ///     Peak working set rounded up to a whole MiB. Kept for readers that predate
    ///     <see cref="PeakWorkingSetBytes" />; new code reads the byte value.
    /// </summary>
    public const string PeakWorkingSetMb = "peak_working_set_mb";
}
