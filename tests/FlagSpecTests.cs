using Lurp.Handlers;

namespace Lurp.Tests;

/// <summary>
///     Checks the CLI flag records (<see cref="FlagSpec" />) that the mode registry is built from:
///     every flag has a usable description, value hints match the flag shape, the repeatable set
///     is exactly the two list flags, the closed value lists are accepted by the parsers, and
///     <c>--help</c> prints every description.
/// </summary>
public sealed class FlagSpecTests
{
    private static readonly string[] ExpectedRepeatableFlags = ["--affected-project=", "--skip-adapter="];

    [Fact]
    public void EveryFlag_HasOneLineDescription()
    {
        var failures = new List<string>();
        foreach (var entry in Program.ModeRegistry)
        {
            foreach (var spec in entry.Specs)
            {
                if (string.IsNullOrWhiteSpace(spec.Description) || spec.Description.Contains('\n') || spec.Description.Contains('\r'))
                    failures.Add($"{entry.Name}:{spec.Name}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ValuedFlags_HaveAHint_BareFlagsHaveNone()
    {
        var failures = new List<string>();
        foreach (var entry in Program.ModeRegistry)
        {
            foreach (var spec in entry.Specs)
            {
                var hasList = spec.AllowedValues is { Count: > 0 };
                if (spec.TakesValue)
                {
                    if (string.IsNullOrWhiteSpace(spec.ValueHint) && !hasList)
                        failures.Add($"{entry.Name}:{spec.Name}");
                }
                else if (spec.ValueHint is not null || spec.AllowedValues is not null)
                {
                    failures.Add($"{entry.Name}:{spec.Name}");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void FlagNames_AreUniquePerMode()
    {
        var failures = new List<string>();
        foreach (var entry in Program.ModeRegistry)
        {
            var names = entry.Specs.Select(static s => s.Name).ToList();
            foreach (var duplicate in names.GroupBy(static n => n, StringComparer.Ordinal).Where(static g => g.Count() > 1))
                failures.Add($"{entry.Name}:{duplicate.Key}");
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Repeatable_IsSetOnlyOnTheTwoListFlags()
    {
        var repeatable = Program.ModeRegistry
            .SelectMany(static e => e.Specs)
            .Where(static s => s.Repeatable)
            .ToList();

        var names = repeatable.Select(static s => s.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedRepeatableFlags, names);
        Assert.All(repeatable, static s => Assert.True(s.TakesValue));
    }

    [Fact]
    public void AllowedValues_OfOutput_AreAcceptedByTheParser()
    {
        foreach (var value in FlagCatalog.Output.AllowedValues!)
            HandlerBootstrap.ParseOutputMode([$"--output={value}"]);

        // Negative control: the parser really rejects a value outside the list.
        var ex = Assert.Throws<CliExitException>(() => HandlerBootstrap.ParseOutputMode(["--output=banana"]));
        Assert.Equal(1, ex.ExitCode);
    }

    [Fact]
    public void AllowedValues_OfFreshness_AreAcceptedByTheParser()
    {
        foreach (var value in FlagCatalog.Freshness.AllowedValues!)
            HandlerBootstrap.ParseFreshnessMode([$"--freshness={value}"]);
    }

    [Fact]
    public void HelpText_PlainText_RemovesBackticks()
    {
        Assert.Equal("Use --x and y.", HelpText.PlainText("Use `--x` and `y`."));
    }

    [Fact]
    public void Help_ContainsNoBacktick()
    {
        Assert.DoesNotContain("`", CaptureHelp());
    }

    [Fact]
    public void Help_ListsADescriptionForEveryFlag()
    {
        var help = CaptureHelp();
        var missing = new List<string>();
        foreach (var entry in Program.ModeRegistry)
        {
            foreach (var spec in entry.Specs)
            {
                var plain = HelpText.PlainText(spec.Description);
                var prefix = plain[..Math.Min(20, plain.Length)];
                if (!help.Contains(prefix, StringComparison.Ordinal))
                    missing.Add($"{entry.Name}:{spec.Name}");
            }
        }

        Assert.Empty(missing);
    }

    private static string CaptureHelp()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            HelpText.PrintHelp();
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }
}
