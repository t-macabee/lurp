using Lurp.Storage;

namespace Lurp.Tests;

public sealed class DeadCandidateClassificationTests
{
    [Fact]
    public void ClassifyCandidate_WeakProvenanceWithoutDedicatedRung_FallsBackToFrameworkConvention()
    {
        var cand = MakeFacts("Private");
        var decl = MakeDecl();
        var incoming = new List<EdgeRecord>
        {
            new() { SourceSymbolId = "src", TargetSymbolId = "sym", Kind = "Calls", Provenance = Provenance.Possible }
        };

        var verdict = DeadCandidateStore.ClassifyCandidate(
            cand, decl, incoming, false, false, false,
            // The fallback rung never reads the binding inputs.
            overlapsBinding: false, bindingRecords: [], projectFacts: null!,
            factsById: [], subtreeByType: [], mapsToTargets: []);

        Assert.NotNull(verdict);
        Assert.Equal(DeadCandidateStatus.UncertainDead, verdict.Status);
        Assert.Equal(DeadCandidateReason.FrameworkConvention, verdict.Reason);
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
            factsById: [], subtreeByType: [], mapsToTargets: []);

        Assert.NotNull(verdict);
        Assert.Equal(DeadCandidateStatus.UncertainDead, verdict.Status);
        Assert.Equal(DeadCandidateReason.EntryPointConvention, verdict.Reason);
    }

    [Fact]
    public void ClassifyCandidate_PublicExternalInterface_WithIncludePublicFalse_IsUncertainDead()
    {
        var cand = MakeFacts("Public", implementsExternalInterface: true);
        var decl = MakeDecl();
        var incoming = new List<EdgeRecord>();

        var verdict = DeadCandidateStore.ClassifyCandidate(
            cand, decl, incoming, false, false, false,
            overlapsBinding: false, bindingRecords: [], projectFacts: null!,
            factsById: [], subtreeByType: [], mapsToTargets: []);

        Assert.NotNull(verdict);
        Assert.Equal(DeadCandidateStatus.UncertainDead, verdict.Status);
        Assert.Equal(DeadCandidateReason.ExternalInterfaceImplementation, verdict.Reason);
    }

    private static DeadCandidateStore.CandidateFacts MakeFacts(
        string accessibility,
        bool isEntryPointMethod = false,
        bool implementsExternalInterface = false,
        string kind = "Method")
    {
        var row = new DeadCandidateStore.CandidateRow("sym", kind, null, null, "doc", "asm");
        return new DeadCandidateStore.CandidateFacts(
            row, null, accessibility, "asm", false, false, false, false,
            implementsExternalInterface, null, null, false, isEntryPointMethod);
    }

    private static DeadCandidateStore.DeclInfo MakeDecl() =>
        new() { IsGenerated = false, Locations = [], DeclarationCount = 0, DocumentPaths = [] };
}