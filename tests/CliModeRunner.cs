using Lurp.Handlers;

namespace Lurp.Tests;

/// <summary>
///     Runs one CLI mode through the registry the way <see cref="Program.Main" /> does:
///     resolves the entry by name, validates the flags, and captures stdout. A
///     <see cref="CliExitException" /> is rethrown as <see cref="InvalidOperationException" />
///     with the mode, the arguments and the captured stdout, so a failed call can never pass
///     as an empty payload.
/// </summary>
internal static class CliModeRunner
{
    internal static async Task<string> RunAsync(string mode, string[] args)
    {
        var entry = Program.FindMode(mode)
            ?? throw new InvalidOperationException($"No Program.ModeRegistry entry named '{mode}'.");

        var stdout = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(stdout);
            CliFlagValidation.Validate(entry, args);
            await entry.Handler(args);
        }
        catch (CliExitException ex)
        {
            throw new InvalidOperationException(
                $"Mode '{mode}' threw CliExitException for args [{string.Join(", ", args)}]. Captured stdout: {stdout}", ex);
        }
        finally
        {
            Console.SetOut(original);
        }

        return stdout.ToString();
    }
}
