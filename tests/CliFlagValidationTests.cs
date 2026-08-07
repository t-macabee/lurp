using System;
using System.Linq;
using Xunit;

namespace Lurp.Storage.Tests;

/// <summary>
/// Covers <c>CliFlagValidation</c> directly. The rejection paths run the built
/// <c>Lurp.dll</c> as a subprocess because they call <see cref="Environment.Exit(int)"/>,
/// which would kill the test host.
/// <para>
/// The positive sweep enumerates <c>Program.ModeRegistry</c> itself (via
/// <c>InternalsVisibleTo</c>), so a flag added to a registry entry is covered without
/// touching this file. Note what that proves: every <em>declared</em> flag passes
/// validation — the inventory is self-consistent with the validator. It cannot prove
/// the declarations match what a handler actually reads; that correspondence was
/// re-derived by tracing handler consumption and only a handler-level test can pin it.
/// </para>
/// </summary>
public sealed class CliFlagValidationTests
{
    public static TheoryData<string, string[]> AllModesWithDeclaredFlags()
    {
        var data = new TheoryData<string, string[]>();
        foreach (var entry in Lurp.Program.ModeRegistry)
        {
            var flags = entry.Flags
                .Select(f => f.EndsWith('=') ? f + "x" : f)
                .ToArray();
            data.Add(entry.Name, flags);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllModesWithDeclaredFlags))]
    public void EveryDeclaredFlag_PassesValidation(string mode, string[] declaredFlags)
    {
        var args = new[] { $"--mode={mode}" }.Concat(declaredFlags).ToArray();

        var (_, _, stdErr) = LurpProcessHarness.Run(args);

        Assert.DoesNotContain("unknown flag", stdErr);
    }

    [Fact]
    public void UnknownFlag_ExitsNonZero_AndNamesTheMode()
    {
        var (exitCode, _, stdErr) = LurpProcessHarness.Run("--mode=diff", "--bogus-flag=1");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--bogus-flag='", stdErr);
        Assert.Contains("--mode=diff", stdErr);
    }

    /// <summary>
    /// Shape mismatch, valued form of a bare flag: <c>--quiet=5</c> must not match the
    /// declared bare <c>--quiet</c>. Before the validator, <c>IsQuiet</c>'s exact
    /// <c>args.Contains("--quiet")</c> silently treated it as not-quiet.
    /// </summary>
    [Fact]
    public void ValuedFormOfBareFlag_IsRejected()
    {
        var (exitCode, _, stdErr) = LurpProcessHarness.Run("--mode=search", "--query=x", "--quiet=5");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--quiet='", stdErr);
    }

    /// <summary>
    /// Shape mismatch, bare form of a valued flag: a bare <c>--budget</c> must not match
    /// the declared <c>--budget=</c>. Before the validator, <c>GetArgValue</c> silently
    /// fell through to the default budget.
    /// </summary>
    [Fact]
    public void BareFormOfValuedFlag_IsRejected()
    {
        var (exitCode, _, stdErr) = LurpProcessHarness.Run("--mode=context", "--symbol=x", "--budget");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--budget'", stdErr);
    }

    [Fact]
    public void EditDistance1Typo_ProducesDidYouMeanLine()
    {
        var (exitCode, _, stdErr) = LurpProcessHarness.Run("--mode=context", "--symbol=x", "--budgett=100");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("unknown flag '--budgett='", stdErr);
        Assert.Contains("Did you mean '--budget='?", stdErr);
    }
}
