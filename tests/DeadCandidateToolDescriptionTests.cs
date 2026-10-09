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

    // Mirrors the check order in DeadCandidateStore.GetDeadCandidatesPage; change both together.
    private static readonly string[] LadderReasonCodesInOrder =
    {
        DeadCandidateReason.BindingIncompleteness,
        DeadCandidateReason.EntryPointConvention,
        DeadCandidateReason.ExternalInterfaceImplementation,
        DeadCandidateReason.PublicSurface,
        DeadCandidateReason.GeneratedExcluded,
        DeadCandidateReason.TestHarness,
        DeadCandidateReason.PossibleDispatch,
        DeadCandidateReason.FrameworkConvention,
        DeadCandidateReason.NameCandidate,
        DeadCandidateReason.RuntimeUnknown,
        DeadCandidateReason.EfConvention,
        DeadCandidateReason.SerializationConvention,
        DeadCandidateReason.NoIncomingLiveEdges
    };

    [Fact]
    public void Description_SuppressionLadder_NamesReasonCodesInCodeOrder()
    {
        var text = ReadDescription();

        const string startAnchor = "Suppression ladder";
        const string endAnchor = "Default excludes";
        var start = text.IndexOf(startAnchor, StringComparison.Ordinal);
        var end = text.IndexOf(endAnchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"description is missing '{startAnchor}'");
        Assert.True(end > start, $"description is missing '{endAnchor}' after '{startAnchor}'");

        var slice = text.Substring(start, end - start);

        var previous = -1;
        foreach (var code in LadderReasonCodesInOrder)
        {
            var position = slice.IndexOf(code, StringComparison.Ordinal);
            Assert.True(position >= 0, $"ladder is missing the reason code '{code}'");
            Assert.True(position > previous, $"reason code '{code}' is out of code order in the ladder");
            previous = position;
        }

        // The order list and the reason constants must be the same set, so a new reason
        // cannot be added without the ladder and this test.
        var constCodes = typeof(DeadCandidateReason)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            constCodes.SetEquals(LadderReasonCodesInOrder),
            "the ladder reason list must be exactly the DeadCandidateReason constants");
    }

    [Fact]
    public void Description_SerializationRule_NamesTheOptInAttributes()
    {
        var text = ReadDescription();

        Assert.Contains("JsonInclude", text);
        Assert.Contains("JsonProperty", text);
        Assert.Contains("DataMember", text);
        Assert.DoesNotContain("attribute-free", text);
    }
}
