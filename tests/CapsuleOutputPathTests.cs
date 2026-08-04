using Lurp.Handlers;
using Lurp.Workspace;

namespace Lurp.Storage.Tests;

public sealed class CapsuleOutputPathTests : IDisposable
{
    private readonly string _outputDir = Path.Combine(Path.GetTempPath(), $"lurp-capsule-output-{Guid.NewGuid():N}");

    [Fact]
    public void LongMethodSymbol_WritesCapsuleToShortStableUniqueFileName()
    {
        var longSymbolId = "M:eNote.Application.Features.Rentals.InstrumentRentals.StateMachine.RentalStateMachine.Fire(eNote.Domain.Entities.Rentals.InstrumentRental,eNote.Application.Features.Rentals.InstrumentRentals.StateMachine.RentalTrigger,eNote.Application.Features.Rentals.InstrumentRentals.StateMachine.RentalTransitionContext)|eNote.Application, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
        var alternateSymbolId = longSymbolId.Replace("RentalTransitionContext", "AlternativeTransitionContext", StringComparison.Ordinal);
        var capsule = new ContextCapsule(new CapsuleAnchor(longSymbolId, "global::Example.Fire", "Method", ""));

        Directory.CreateDirectory(_outputDir);
        ContextHandler.WriteCapsuleOutput(capsule, _outputDir, OutputMode.Json, quiet: true);

        var outputPath = ContextHandler.GetCapsuleOutputPath(_outputDir, longSymbolId);
        var alternatePath = ContextHandler.GetCapsuleOutputPath(_outputDir, alternateSymbolId);
        var fileName = Path.GetFileName(outputPath);

        Assert.True(File.Exists(outputPath));
        Assert.True(fileName.Length <= 128, $"Filename length was {fileName.Length}: {fileName}");
        Assert.Contains("RentalStateMachine.Fire", fileName, StringComparison.Ordinal);
        Assert.NotEqual(outputPath, alternatePath);
    }

    [Fact]
    public void Summary_ExplainsContentAndDeliveryEstimates_AndTierRecovery()
    {
        var capsule = new ContextCapsule(new CapsuleAnchor("M:Example.Service.Run|asm", "global::Example.Service.Run", "Method", ""))
        {
            Budget = 4_000,
            EstimatedTokens = 3_600,
            EstimatedArtifactTokens = 9_200,
            Truncated = true,
        };
        capsule.OmittedTiers.Add(new TruncationEntry("directCallers", "budget_exhausted"));

        Directory.CreateDirectory(_outputDir);
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            ContextHandler.WriteCapsuleOutput(capsule, _outputDir, OutputMode.Summary, quiet: false);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var summary = captured.ToString();
        Assert.Contains("content tokens:  3600/4000", summary, StringComparison.Ordinal);
        Assert.Contains("delivery tokens: ~9200", summary, StringComparison.Ordinal);
        Assert.Contains("size the context window from this", summary, StringComparison.Ordinal);
        Assert.Contains("fetch with --tier=directCallers", summary, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }
}
