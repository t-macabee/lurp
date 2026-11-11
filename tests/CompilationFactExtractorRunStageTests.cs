using Lurp.Storage;
using Lurp.Workspace;

namespace Lurp.Tests;

public sealed class CompilationFactExtractorRunStageTests
{
    private static CompilationFactExtractor.StageContext NewContext(string projectName = "TestProject")
    {
        return new CompilationFactExtractor.StageContext(projectName,
            [],
            new BindingIncompletenessCollector(projectName, "/repo"),
            ["/repo/src/A.cs", "/repo/src/B.cs"]);
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

        // D34: one extractor_failure row on each document of the project.
        var rows = ctx.Incompleteness.ToRecords();
        Assert.Equal(["src/A.cs", "src/B.cs"], rows.Select(static r => r.DocumentPath));
        Assert.All(rows, static r => Assert.Equal(BindingIncompletenessReason.ExtractorFailure, r.Reason));
        Assert.All(rows, static r => Assert.Equal(1, r.Count));
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
        Assert.Equal(2, ctx.Incompleteness.ToRecords().Count);
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
            ctx, "StructuralEdge", null, null,
            msg => $"Edge extraction failed for project 'TestProject': {msg}",
            () => throw new InvalidOperationException("structural boom"),
            []);

        Assert.Empty(result);
        var failure = Assert.Single(ctx.Failures);
        Assert.Equal("StructuralEdge", failure.Stage);
        Assert.Equal("structural boom", failure.Message);
        Assert.Equal(2, ctx.Incompleteness.ToRecords().Count);
    }

    [Fact]
    public void RunStage_ProjectWithNoDocument_RecordsOneProjectLevelRow()
    {
        var ctx = new CompilationFactExtractor.StageContext("TestProject", [],
            new BindingIncompletenessCollector("TestProject", "/repo"), []);

        CompilationFactExtractor.RunStage(ctx, "Reflection", null, null, msg => msg,
            () => throw new InvalidOperationException("boom"));

        var row = Assert.Single(ctx.Incompleteness.ToRecords());
        Assert.Null(row.DocumentPath);
        Assert.Equal(BindingIncompletenessReason.ExtractorFailure, row.Reason);
    }

    [Fact]
    public void RunStage_Generic_StageSucceeds_ReturnsStageResult()
    {
        var ctx = NewContext();

        var result = CompilationFactExtractor.RunStage(
            ctx, "StructuralEdge", null, null, msg => msg,
            () => new List<int> { 1, 2, 3 },
            []);

        Assert.Equal([1, 2, 3], result);
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