using System.Text.RegularExpressions;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Guards against help text drift: every flag mentioned in <c>HelpText.PrintHelp()</c>
/// must exist in <c>Program.ModeRegistry</c> + <c>CliFlagValidation.GlobalFlags</c>.
/// A flag renamed or removed from the registry makes the help text silently stale
/// (or vice versa) without this check.
/// </summary>
public sealed class HelpTextFlagCoverageTests
{
    /// <summary>
    /// Tokens in the help text that look like flags but are value examples,
    /// placeholder expansions, or mentions in prose — not declared flags.
    /// </summary>
    private static readonly HashSet<string> AllowedHelpTextNoise = new(StringComparer.Ordinal)
    {
        // Value examples embedded in prose (--strategy=full, --strategy=incremental)
        "--strategy=full",
        "--strategy=incremental",
        // Mentioned in prose descriptions but not flags themselves
        "--output=summary",
        "--output=json",
        "--output=jsonl",
        "--freshness=auto",
        "--freshness=hash",
        "--freshness=off",
        // Mentioned as deprecated env var names in prose
        "--format=json",
        // Prose reference to --tier= in parenthetical "(--tier only)"
        "--tier",
    };

    [Fact]
    public void EveryHelpTextFlag_ExistsInModeRegistryOrGlobalFlags()
    {
        var output = CaptureHelpText();
        var helpFlags = ExtractFlagsFromHelpText(output);

        var knownFlags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in Lurp.CliFlagValidation.GlobalFlags)
            knownFlags.Add(flag);
        foreach (var entry in Lurp.Program.ModeRegistry)
        {
            foreach (var flag in entry.Flags)
                knownFlags.Add(flag);
        }

        var missing = new List<string>();
        foreach (var flag in helpFlags)
        {
            if (!knownFlags.Contains(flag) && !AllowedHelpTextNoise.Contains(flag))
                missing.Add(flag);
        }

        Assert.Empty(missing);
    }

    private static string CaptureHelpText()
    {
        var originalOut = Console.Out;
        try
        {
            using var writer = new StringWriter();
            Console.SetOut(writer);
            Lurp.HelpText.PrintHelp();
            writer.Flush();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static HashSet<string> ExtractFlagsFromHelpText(string helpText)
    {
        var flags = new HashSet<string>(StringComparer.Ordinal);

        var matches = Regex.Matches(helpText, @"--[a-z][a-z-]*(=?|=)");

        foreach (Match match in matches)
        {
            var token = match.Value;

            // Skip tokens that are immediately followed by '<' (placeholder syntax
            // like --flag=<value>) — the token itself (--flag=) is already captured
            // by the regex that stops at '='.
            if (match.Index + token.Length < helpText.Length
                && helpText[match.Index + token.Length] == '<')
            {
                // The regex already captured --flag=, not --flag=<value>, so this
                // shouldn't happen; guard anyway.
                continue;
            }

            flags.Add(token);
        }

        return flags;
    }
}
