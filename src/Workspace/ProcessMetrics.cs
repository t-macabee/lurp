using System.Diagnostics;
using Lurp.Storage;

namespace Lurp.Workspace;

internal static class ProcessMetrics
{
    private const long BytesPerMiB = 1024 * 1024;

    internal static long PeakWorkingSetBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PeakWorkingSet64;
    }

    internal static long ToMegabytes(long bytes) => (bytes + (BytesPerMiB - 1)) / BytesPerMiB;

    /// <summary>
    ///     The snapshot metrics for this process. <c>peak_working_set_bytes</c> is the
    ///     exact value; <c>peak_working_set_mb</c> is a display and compatibility value.
    /// </summary>
    internal static Dictionary<string, long> PeakWorkingSetSnapshotMetrics()
    {
        var bytes = PeakWorkingSetBytes();
        return new Dictionary<string, long>
        {
            [SnapshotMetricNames.PeakWorkingSetBytes] = bytes,
            [SnapshotMetricNames.PeakWorkingSetMb] = ToMegabytes(bytes)
        };
    }
}
