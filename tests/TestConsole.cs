using Lurp.Handlers;

namespace Lurp.Tests;

internal static class TestConsole
{
    internal static CaptureResult Capture(Action action) => CaptureAsync(() => { action(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    internal static async Task<CaptureResult> CaptureAsync(Func<Task> action)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        CliExitException? failure = null;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            await action();
        }
        catch (CliExitException ex)
        {
            failure = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return new CaptureResult(stdout.ToString(), stderr.ToString(), failure);
    }
}

internal sealed record CaptureResult(string Stdout, string Stderr, CliExitException? Failure);
