using Lurp.Storage;
using Microsoft.CodeAnalysis;

namespace Lurp.Workspace;

public sealed class MemberEdgeExtractor
{
    private readonly List<IMemberEdgeExtractor> _extractors;
    public List<CompilationFactExtractor.ExtractionMeasurement> Measurements { get; } = [];

    public MemberEdgeExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions, IReadOnlySet<DocumentId> generatedDocuments, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments = null)
        : this(compilation, documentVersions, generatedDocuments, snapshotId, gitRoot, scopeDocuments, null)
    {
    }

    internal MemberEdgeExtractor(Compilation compilation, IReadOnlyDictionary<DocumentId, DocumentVersionId> documentVersions, IReadOnlySet<DocumentId> generatedDocuments, string snapshotId, string gitRoot, IReadOnlySet<string>? scopeDocuments, BindingIncompletenessCollector? incompleteness)
    {
        var context = new MemberEdgeExtractionContext(compilation, documentVersions, generatedDocuments, snapshotId, gitRoot, scopeDocuments, incompleteness);

        _extractors =
        [
            new DeclaresEdgeExtractor(context),
            new CallsEdgeExtractor(context),
            new ConstructsEdgeExtractor(context),
            new OverridesEdgeExtractor(context),
            new ReadsWritesEdgeExtractor(context),
            new ReturnsEdgeExtractor(context),
            new ParameterDependencyEdgeExtractor(context),
            new ThrowsEdgeExtractor(context),
        ];
    }

    public List<EdgeRecord> ExtractAll()
    {
        var allEdges = new List<EdgeRecord>();

        foreach (var extractor in _extractors)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
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
