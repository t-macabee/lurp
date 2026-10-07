using System.ComponentModel;
using System.Reflection;
using Lurp.Mcp.Tools;
using Lurp.Storage;

namespace Lurp.Tests;

/// <summary>
///     Pins the <c>lurp_dead_candidates</c> tool description to the shared
///     dead-candidate vocabulary, so a kind removed from
///     <see cref="DeadCandidateLiveness" /> cannot linger in the description.
/// </summary>
public sealed class DeadCandidateToolDescriptionTests
{
    private static string ReadDescription()
    {
        var method = typeof(DeadCandidatesTool).GetMethod(
            "LurpDeadCandidates",
            BindingFlags.Public | BindingFlags.Instance)!;
        var attribute = method.GetCustomAttribute<DescriptionAttribute>()!;
        return attribute.Description;
    }

    [Fact]
    public void Description_NamesExactlyTheSharedLiveKindsAndStrongProvenance()
    {
        var text = ReadDescription();

        foreach (var kind in DeadCandidateLiveness.LiveEdgeKinds)
            Assert.Contains(kind, text);

        foreach (var provenance in DeadCandidateLiveness.StrongProvenance)
            Assert.Contains(provenance, text);

        // B25 output: the description names the type-use kinds too.
        foreach (var kind in DeadCandidateLiveness.TypeUseEdgeKinds)
            Assert.Contains(kind, text);

        // The parenthesised list right after "no incoming LIVE edge" names the live kinds.
        const string anchor = "no incoming LIVE edge (";
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"description is missing '{anchor}'");
        start += anchor.Length;
        var end = text.IndexOf(')', start);
        Assert.True(end > start, "description has no closing ')' after the live-kind list");
        var listed = text.Substring(start, end - start);

        // An exact compare catches a removed kind, a reordered list and a duplicate; a
        // substring check would miss all three.
        Assert.Equal(DeadCandidateLiveness.LiveEdgeKinds, listed.Split(", "));

        // F14: the description states the reference rule, not a project-name rule.
        Assert.Contains("references a test framework", text);
    }
}
