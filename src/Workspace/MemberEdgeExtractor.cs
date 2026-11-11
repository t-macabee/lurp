using Microsoft.CodeAnalysis;
using System.Diagnostics;

namespace Lurp.Workspace;

public sealed class MemberEdgeExtractor
{
    private readonly List<IMemberEdgeExtractor> _extractors;

    internal MemberEdgeExtractor(ProjectExtractionInputs inputs)
    {
        var context = new MemberEdgeExtractionContext(inputs);

        _extractors =
        [
            new DeclaresEdgeExtractor(context),
            new OverridesEdgeExtractor(context),
            new ReturnsEdgeExtractor(context),
            new ParameterDependencyEdgeExtractor(context),
            new ThrowsEdgeExtractor(context),
            new OperationShapeExtractor(context)
        ];
    }

    public List<CompilationFactExtractor.ExtractionMeasurement> Measurements { get; } = [];

    public List<EdgeRecord> ExtractAll()
    {
        var allEdges = new List<EdgeRecord>();

        foreach (var extractor in _extractors)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            allEdges.AddRange(extractor.Extract());
            stopwatch.Stop();
            Measurements.Add(new CompilationFactExtractor.ExtractionMeasurement(
                extractor.GetType().Name,
                stopwatch.ElapsedMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
        }

        return allEdges;
    }
}