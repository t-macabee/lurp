using System.Diagnostics;

namespace Lurp.Workspace;

internal static class ProcessMetrics
{
    internal static long PeakWorkingSetMb()
    {
        using var process = Process.GetCurrentProcess();
        return (process.PeakWorkingSet64 + (1024 * 1024 - 1)) / (1024 * 1024);
    }
}
