using System.Diagnostics;

namespace Lurp.PerfHarness;

internal sealed class PeakSampler
{
    private readonly Process _process;
    private readonly object _sync = new();
    private Task? _task;
    private volatile bool _stop;
    private long _peakBytes;

    public PeakSampler(Process process) => _process = process;

    public void Start()
    {
        _task = Task.Run(() =>
        {
            while (!_stop)
            {
                var value = ProcessRunner.SamplePeak(_process);
                lock (_sync)
                {
                    if (value > _peakBytes)
                        _peakBytes = value;
                }

                Thread.Sleep(ProcessRunner.PollIntervalMs);
            }
        });
    }

    public void Stop()
    {
        _stop = true;
        try
        {
            _task?.Wait(5000);
        }
        catch (AggregateException)
        {
        }
    }

    public long PeakMb
    {
        get
        {
            lock (_sync)
                return ProcessRunner.ToMegabytes(_peakBytes);
        }
    }
}
