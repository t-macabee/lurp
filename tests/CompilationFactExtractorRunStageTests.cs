using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class CompilationFactExtractorRunStageTests
{
    private static CompilationFactExtractor.StageContext NewContext(string projectName = "TestProject")
    {
        return new CompilationFactExtractor.StageContext(projectName,
            new List<CompilationFactExtractor.ExtractionFailure>(),
            new BindingIncompletenessCollector(projectName, "/repo"));
    }

    [Fact]
    public void RunStage_Void_StageThrows_RecordsFailureAndIncompleteness()
    {
        var ctx = NewContext();
        string? logged = null;

        CompilationFactExtractor.RunStage(
            ctx, "MemberEdge", null, msg => logged = msg,
            msg => $"Member edge extraction failed for project 'TestProject': {msg}",
            () => throw new InvalidOperationException("boom"));

        var failure = Assert.Single(ctx.Failures);
        Assert.Equal("MemberEdge", failure.Stage);
        Assert.Equal("TestProject", failure.ProjectName);
        Assert.Null(failure.AdapterName);
        Assert.Equal("boom", failure.Message);
        Assert.Equal("Member edge extraction failed for project 'TestProject': boom", logged);

        var incompleteness = Assert.Single(ctx.Incompleteness.ToRecords());
        Assert.Equal(BindingIncompletenessReason.ExtractorFailure, incompleteness.Reason);
    }

    [Fact]
    public void RunStage_Void_PolymorphismStageThrows_IsRecordedInsteadOfEscaping()
    {
        var ctx = NewContext();

        var ex = Record.Exception(() => CompilationFactExtractor.RunStage(
            ctx, "Polymorphism", null, null,
            msg => $"Polymorphism extraction failed for project 'TestProject': {msg}",
            () => throw new InvalidOperationException("polymorphism boom")));

        Assert.Null(ex);
        var failure = Assert.Single(ctx.Failures);
        Assert.Equal("Polymorphism", failure.Stage);
        Assert.Equal("polymorphism boom", failure.Message);
        Assert.Single(ctx.Incompleteness.ToRecords());
    }

    [Fact]
    public void RunStage_Void_StageSucceeds_RecordsNothing()
    {
        var ctx = NewContext();
        var ran = false;

        CompilationFactExtractor.RunStage(ctx, "Reflection", null, null, msg => msg, () => ran = true);

        Assert.True(ran);
        Assert.Empty(ctx.Failures);
        Assert.Empty(ctx.Incompleteness.ToRecords());
    }

    [Fact]
    public void RunStage_Generic_StageThrows_ReturnsFallbackAndRecordsFailure()
    {
        var ctx = NewContext();

        var result = CompilationFactExtractor.RunStage<List<string>>(
            ctx, "SymbolDeclaration", null, null,
            msg => $"Symbol extraction failed for project 'TestProject': {msg}",
            () => throw new InvalidOperationException("symbol boom"),
            new List<string>());

        Assert.Empty(result);
        var failure = Assert.Single(ctx.Failures);
        Assert.Equal("SymbolDeclaration", failure.Stage);
        Assert.Equal("symbol boom", failure.Message);
        Assert.Single(ctx.Incompleteness.ToRecords());
    }

    [Fact]
    public void RunStage_Generic_StageSucceeds_ReturnsStageResult()
    {
        var ctx = NewContext();

        var result = CompilationFactExtractor.RunStage(
            ctx, "StructuralEdge", null, null, msg => msg,
            () => new List<int> { 1, 2, 3 },
            new List<int>());

        Assert.Equal(new List<int> { 1, 2, 3 }, result);
        Assert.Empty(ctx.Failures);
        Assert.Empty(ctx.Incompleteness.ToRecords());
    }

    [Fact]
    public void RunStage_AdapterStage_RecordsAdapterName()
    {
        var ctx = NewContext();

        CompilationFactExtractor.RunStage(
            ctx, "Adapter", "ThrowingAdapter", null,
            msg => $"Adapter 'ThrowingAdapter' failed: {msg}",
            () => throw new InvalidOperationException("adapter boom"));

        var failure = Assert.Single(ctx.Failures);
        Assert.Equal("Adapter", failure.Stage);
        Assert.Equal("ThrowingAdapter", failure.AdapterName);
    }
}