using Lurp.Storage;
using Microsoft.Data.Sqlite;

namespace Lurp.Tests;

public sealed class DeadCandidateClassificationTests
{
    [Fact]
    public void ClassifyCandidate_UnknownWeakProvenance_Throws()
    {
        var cand = MakeFacts("Private");
        var decl = MakeDecl();
        var incoming = new List<EdgeRecord>
        {
            new() { SourceSymbolId = "src", TargetSymbolId = "sym", Kind = "Calls", Provenance = "bogus" }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => DeadCandidateStore.ClassifyCandidate(
            cand, decl, incoming, false, false, false,
            overlapsBinding: false, bindingRecords: [], projectFacts: null!,
            factsById: [], subtreeByType: [], conventions: new()));

        Assert.Contains("src", ex.Message);
        Assert.Contains("sym", ex.Message);
        Assert.Contains("Calls", ex.Message);
        Assert.Contains("bogus", ex.Message);
    }

    [Theory]
    [InlineData(nameof(EdgeKind.MayDispatchTo))]
    [InlineData(nameof(EdgeKind.Calls))]
    public void ClassifyCandidate_PossibleProvenance_IsPossibleDispatch(string edgeKind)
    {
        var cand = MakeFacts("Private");
        var decl = MakeDecl();
        var incoming = new List<EdgeRecord>
        {
            new() { SourceSymbolId = "src", TargetSymbolId = "sym", Kind = edgeKind, Provenance = Provenance.Possible }
        };

        var verdict = DeadCandidateStore.ClassifyCandidate(
            cand, decl, incoming, false, false, false,
            overlapsBinding: false, bindingRecords: [], projectFacts: null!,
            factsById: [], subtreeByType: [], conventions: new());

        Assert.NotNull(verdict);
        Assert.Equal(DeadCandidateStatus.UncertainDead, verdict.Status);
        Assert.Equal(DeadCandidateReason.PossibleDispatch, verdict.Reason);
    }

    [Fact]
    public void GetDeadCandidatesPage_ZeroLimit_ThrowsArgumentOutOfRange()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        var store = new DeadCandidateStore(conn);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.GetDeadCandidatesPage("snap", null, null, null, false, false, false, 0, null));
    }

    [Fact]
    public void ClassifyCandidate_PublicEntryPoint_WithIncludePublicFalse_IsUncertainDead()
    {
        var cand = MakeFacts("Public", isEntryPointMethod: true);
        var decl = MakeDecl();
        var incoming = new List<EdgeRecord>();

        var verdict = DeadCandidateStore.ClassifyCandidate(
            cand, decl, incoming, false, false, false,
            overlapsBinding: false, bindingRecords: [], projectFacts: null!,
            factsById: [], subtreeByType: [], conventions: new());

        Assert.NotNull(verdict);
        Assert.Equal(DeadCandidateStatus.UncertainDead, verdict.Status);
        Assert.Equal(DeadCandidateReason.EntryPointConvention, verdict.Reason);
    }

    [Fact]
    public void ClassifyCandidate_PublicExternalInterface_WithIncludePublicFalse_IsUncertainDead()
    {
        var cand = MakeFacts("Public", implementsExternalMember: true);
        var decl = MakeDecl();
        var incoming = new List<EdgeRecord>();

        var verdict = DeadCandidateStore.ClassifyCandidate(
            cand, decl, incoming, false, false, false,
            overlapsBinding: false, bindingRecords: [], projectFacts: null!,
            factsById: [], subtreeByType: [], conventions: new());

        Assert.NotNull(verdict);
        Assert.Equal(DeadCandidateStatus.UncertainDead, verdict.Status);
        Assert.Equal(DeadCandidateReason.ExternalInterfaceImplementation, verdict.Reason);
    }

    private static DeadCandidateStore.CandidateFacts MakeFacts(
        string accessibility,
        bool isEntryPointMethod = false,
        bool implementsExternalMember = false,
        string kind = "Method",
        string? effectiveAccessibility = null)
    {
        var row = new DeadCandidateStore.CandidateRow("sym", kind, null, null, "doc", "asm");
        return new DeadCandidateStore.CandidateFacts(
            row, null, accessibility, effectiveAccessibility ?? accessibility, "asm", false, false, false, false,
            implementsExternalMember, null, null, false, isEntryPointMethod);
    }

    private static DeadCandidateStore.DeclInfo MakeDecl() =>
        new() { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
}