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

    public void Dispose()
    {
        if (Directory.Exists(_outputDir))
            Directory.Delete(_outputDir, recursive: true);
    }
}
