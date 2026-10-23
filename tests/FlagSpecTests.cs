using System.Reflection;
using System.Text.RegularExpressions;
using Lurp.Handlers;
using Lurp.Workspace;

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
    public async Task AllowedValues_OfDirection_AreAcceptedByTheParser()
    {
        var outputDir = TempOutputDir();
        foreach (var value in FlagCatalog.Direction.AllowedValues!)
            Assert.Contains("Index database not found",
                await RunToFailureAsync("impact", ["--symbol=x", $"--direction={value}", $"--output-dir={outputDir}"]));

        Assert.DoesNotContain("Index database not found",
            await RunToFailureAsync("impact", ["--symbol=x", "--direction=banana", $"--output-dir={outputDir}"]));
    }

    [Fact]
    public async Task AllowedValues_OfType_AreAcceptedByTheParser()
    {
        var outputDir = TempOutputDir();
        foreach (var value in FlagCatalog.Type.AllowedValues!)
            Assert.Contains("Index database not found",
                await RunToFailureAsync("search", ["--query=x", $"--type={value}", $"--output-dir={outputDir}"]));

        Assert.DoesNotContain("Index database not found",
            await RunToFailureAsync("search", ["--query=x", "--type=banana", $"--output-dir={outputDir}"]));
    }

    [Fact]
    public async Task AllowedValues_OfIntent_AreAcceptedByTheParser()
    {
        var outputDir = TempOutputDir();
        foreach (var value in FlagCatalog.Intent.AllowedValues!)
            Assert.Contains("Index database not found",
                await RunToFailureAsync("context", ["--symbol=T:X|A", $"--intent={value}", $"--output-dir={outputDir}"]));

        Assert.DoesNotContain("Index database not found",
            await RunToFailureAsync("context", ["--symbol=T:X|A", "--intent=banana", $"--output-dir={outputDir}"]));
    }

    [Fact]
    public void AllowedValues_OfDetail_AreAcceptedByTheParser()
    {
        foreach (var value in FlagCatalog.Detail.AllowedValues!)
            Assert.True(StatusSections.TryParse(value, allowSurfaceNames: false, out _, out _));

        Assert.False(StatusSections.TryParse("banana", allowSurfaceNames: false, out _, out _));
    }

    [Fact]
    public void AllowedValues_OfView_AreAcceptedByTheParser()
    {
        foreach (var value in FlagCatalog.View.AllowedValues!)
            GetSymbolHandler.ResolveViewSelection(value, null);

        Assert.Throws<CliExitException>(() => GetSymbolHandler.ResolveViewSelection("banana", null));
    }

    [Fact]
    public void AllowedValues_OfTheSingleDocumentOutputCopies_AreAcceptedByTheParser()
    {
        foreach (var mode in new[] { "status", "pin-snapshot" })
        {
            var spec = Program.FindMode(mode)!.Specs.Single(candidate => candidate.Name == "--output=");
            foreach (var value in spec.AllowedValues!)
                HandlerBootstrap.ParseOutputMode([$"--output={value}"], allowJsonl: false, defaultMode: OutputMode.Summary);
        }

        var ex = Assert.Throws<CliExitException>(
            () => HandlerBootstrap.ParseOutputMode(["--output=banana"], allowJsonl: false, defaultMode: OutputMode.Summary));
        Assert.Equal(1, ex.ExitCode);
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
    public void Help_PrintsTheCacheRootOfThisOs()
    {
        var help = CaptureHelp();
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "lurp",
            "<hash of the solution path>");

        Assert.Contains(expected, help);
        Assert.DoesNotContain("%LOCALAPPDATA%", help);
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

    [Fact]
    public void Help_ShowsRequirement()
    {
        Assert.Contains("Base snapshot ID. Required.", HelpBlock("diff"));
        Assert.Contains("Required (or set LURP_SOLUTION_PATH).", HelpBlock("index"));
        Assert.Contains("Required in some combinations", HelpBlock("context"));
    }

    [Fact]
    public void Help_ShowsAllowedValues()
    {
        Assert.Contains("Values: all, source, symbol.", HelpBlock("search"));
        Assert.Contains("Values: inspect, modify, diagnose.", HelpBlock("context"));
    }

    [Fact]
    public void Help_ShowsSnapshotPinAndForceDeletion()
    {
        Assert.Contains("The literal latest means the same.", HelpBlock("search"));
        Assert.Contains("which deletes the identical snapshot", HelpBlock("index"));
    }

    [Fact]
    public void Descriptions_UseOnlyBacktickMarkup()
    {
        var failures = new List<string>();
        foreach (var entry in Program.ModeRegistry)
        {
            foreach (var spec in entry.Specs)
            {
                // Text inside a backtick span is code (flag names, globs, values), not prose:
                // --help strips the backticks and prints it as written. Only Markdown in the
                // remaining prose is a defect (globs like obj/** are not bold).
                var prose = Regex.Replace(spec.Description, "`[^`]*`", "");
                if (prose.Contains("**", StringComparison.Ordinal)
                    || prose.Contains("](", StringComparison.Ordinal)
                    || prose.Contains("<br", StringComparison.Ordinal))
                    failures.Add($"{entry.Name}:{spec.Name}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void EveryCatalogDescription_IsRenderedByAMode()
    {
        var rendered = Program.ModeRegistry
            .SelectMany(static entry => entry.Specs)
            .Select(static spec => spec.Description)
            .ToHashSet(StringComparer.Ordinal);

        var failures = typeof(FlagCatalog)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(FlagSpec))
            .Where(field => field.GetValue(null) is FlagSpec spec && !rendered.Contains(spec.Description))
            .Select(field => field.Name)
            .ToList();

        Assert.Empty(failures);
    }

    // The --help layout (HelpText.PrintHelp): a blank line, then "  --mode=<name>", then one
    // line per flag (continuation lines indented), then an optional ModeNotes paragraph, then a
    // blank line. The block is everything from the mode line to the first blank line, with
    // whitespace runs collapsed to one space.
    private static string HelpBlock(string mode)
    {
        var lines = CaptureHelp().Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, line => string.Equals(line.TrimEnd(), $"  --mode={mode}", StringComparison.Ordinal));
        Assert.True(start >= 0, $"--help has no '{mode}' block in OPTIONS BY MODE.");

        var block = new List<string> { lines[start] };
        for (var index = start + 1; index < lines.Length && lines[index].Trim().Length > 0; index++)
            block.Add(lines[index]);

        return Regex.Replace(string.Join(' ', block), @"\s+", " ").Trim();
    }

    // A handler whose value check runs before its database check: a valid value gets past the
    // parser and fails opening the (absent) database, so the failure names the database. An
    // invalid value fails earlier with the flag's own error.
    private static async Task<string> RunToFailureAsync(string mode, string[] args)
    {
        var entry = Program.FindMode(mode) ?? throw new InvalidOperationException($"No mode '{mode}' in Program.ModeRegistry.");
        var ex = await Assert.ThrowsAsync<CliExitException>(() => entry.Handler(args));
        return ex.Message;
    }

    private static string TempOutputDir() =>
        Path.Combine(Path.GetTempPath(), "lurp-flagspec-" + Guid.NewGuid().ToString("N"));

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
